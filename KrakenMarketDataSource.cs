using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TradingApp;

// The live IMarketDataSource: real order books from Kraken's public WebSocket API (v2).
// Market data is free and needs no account or API key. Docs: https://docs.kraken.com/api/docs/websocket-v2/book
//
// Same publishing pattern as FakeMarketDataSource:
//
//   WebSocket receive loop (background thread)
//        |  applies each message to a PRIVATE working book (LocalOrderBook)
//        |  publishes an immutable OrderBookSnapshot
//        v
//   _books (ConcurrentDictionary)  <--  the UI polls this ~60 times a second
//
// Unlike the fake, Kraken sends one full snapshot followed by a stream of CHANGES ("level 84547.7
// now has size 0.72"; size 0 means "remove this level"), so we keep our own copy of each book and
// apply every change to it before publishing.
//
// NOT DONE (would be needed before trusting this with real money):
//   * Kraken sends a CRC32 checksum with each update to detect a corrupted local book. We don't
//     verify it. The simple fix: on a mismatch, resubscribe to get a fresh snapshot.
//   * Rate-limit handling, authentication and order entry. This is read-only market data.
public sealed class KrakenMarketDataSource : IMarketDataSource
{
    private const string Url = "wss://ws.kraken.com/v2";
    private const int Depth = 25;   // price levels per side. Kraken allows 10, 25, 100, 500 or 1000.

    // Kraken's v2 symbol names. Add more here and the whole UI picks them up.
    private static readonly string[] Symbols = { "BTC/USD", "ETH/USD", "SOL/USD", "XRP/USD", "DOGE/USD" };

    // SHARED with the UI thread: latest finished snapshot per symbol.
    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _books = new();

    // RECEIVE-LOOP-ONLY: the working books we apply changes to. The UI never sees these.
    private readonly Dictionary<string, LocalOrderBook> _local = new();

    private readonly CancellationTokenSource _cts = new();   // cancelled by Dispose(); stops all network work
    private IReadOnlyList<Instrument> _instruments = Array.Empty<Instrument>();
    private long _updateCount;

    public event EventHandler<string>? StatusChanged;

    public long UpdateCount => Interlocked.Read(ref _updateCount);
    public IReadOnlyList<Instrument> GetInstruments() => _instruments;
    public OrderBookSnapshot? GetLatestBook(string symbol) => _books.TryGetValue(symbol, out var book) ? book : null;
    public void Dispose() => _cts.Cancel();

    // ---- Startup: connect, learn tick sizes, subscribe, then hand off to the receive loop ----

    // 'async Task' means the method can pause at each 'await' without blocking its thread, and
    // resume when the awaited work finishes.
    //
    // Every await in this class uses .ConfigureAwait(false). Without it, an await started on the
    // UI thread resumes on the UI thread. This class never touches the UI, so it opts out and
    // continues on a thread-pool thread instead, keeping the UI thread free.
    public async Task StartAsync()
    {
        var token = _cts.Token;
        RaiseStatus("Connecting to Kraken...");

        var socket = await ConnectAsync(token).ConfigureAwait(false);

        // 1) Ask for the instrument list to learn each symbol's tick size. Give up after 15s
        //    rather than leave the app stuck at startup. The 'when' filter turns only OUR timeout
        //    into a TimeoutException; a cancellation from Dispose() passes through unchanged.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await SendAsync(socket, "{\"method\":\"subscribe\",\"params\":{\"channel\":\"instrument\"}}", timeout.Token).ConfigureAwait(false);
            _instruments = await ReadInstrumentsAsync(socket, timeout.Token).ConfigureAwait(false);
            await SendAsync(socket, "{\"method\":\"unsubscribe\",\"params\":{\"channel\":\"instrument\"}}", token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Kraken did not send instrument info within 15 seconds.");
        }

        if (_instruments.Count == 0)
            throw new InvalidOperationException("None of the configured symbols were found on Kraken.");

        foreach (var instrument in _instruments)
            _local[instrument.Symbol] = new LocalOrderBook(instrument, Depth);

        // 2) Subscribe to the order books.
        await SubscribeBooksAsync(socket, token).ConfigureAwait(false);
        RaiseStatus("Connected to Kraken");

        // 3) Hand the socket to a loop that runs for the life of the app. Task.Run starts it on a
        //    thread-pool thread. We don't await it (it only ends after Dispose()); '_ =' discards
        //    the Task on purpose. It catches its own exceptions, so none go unobserved.
        _ = Task.Run(() => RunAsync(socket, token));
    }

    private static async Task<ClientWebSocket> ConnectAsync(CancellationToken token)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(Url), token).ConfigureAwait(false);
        return socket;
    }

    private static Task SubscribeBooksAsync(ClientWebSocket socket, CancellationToken token)
    {
        string symbolList = string.Join(",", Symbols.Select(s => "\"" + s + "\""));
        string message = "{\"method\":\"subscribe\",\"params\":{\"channel\":\"book\",\"symbol\":[" + symbolList + "],\"depth\":" + Depth + "}}";
        return SendAsync(socket, message, token);
    }

    // Waits for the one big "instrument snapshot" message and pulls out what we need.
    private static async Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (true)
        {
            using var doc = await ReceiveAsync(socket, token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Kraken closed the connection during startup.");
            var root = doc.RootElement;

            if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "instrument") continue;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "snapshot") continue;

            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array) data = data[0];   // accept an object or a one-item array

            var found = new Dictionary<string, Instrument>();
            foreach (var pair in data.GetProperty("pairs").EnumerateArray())
            {
                string symbol = pair.GetProperty("symbol").GetString()!;
                if (!Symbols.Contains(symbol)) continue;

                // 'price_increment' is the current field; 'tick_size' is the deprecated older one.
                double tick = pair.TryGetProperty("price_increment", out var increment)
                    ? increment.GetDouble()
                    : pair.GetProperty("tick_size").GetDouble();
                found[symbol] = new Instrument(symbol, tick);
            }

            // Keep our own configured order (not whatever order Kraken sends).
            return Symbols.Where(found.ContainsKey).Select(s => found[s]).ToList();
        }
    }

    // ---- The receive loop ----

    // Runs on a thread-pool thread for the life of the feed. If the connection drops, it retries
    // every 3 seconds until it reconnects and resubscribes. Kraken then sends fresh snapshots,
    // which replace our local books.
    private async Task RunAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ReadLoopAsync(socket, token).ConfigureAwait(false);
                RaiseStatus("Kraken closed the connection.");
            }
            catch (OperationCanceledException) { return; }   // Dispose() was called: exit quietly
            catch (Exception ex) { RaiseStatus("Connection error: " + ex.Message); }

            socket.Dispose();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    RaiseStatus("Reconnecting in 3s...");
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                    socket = await ConnectAsync(token).ConfigureAwait(false);
                    await SubscribeBooksAsync(socket, token).ConfigureAwait(false);
                    RaiseStatus("Connected to Kraken");
                    break;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { RaiseStatus("Reconnect failed: " + ex.Message); }
            }
        }
    }

    // Reads messages until the server closes the connection (returns) or something breaks (throws).
    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (true)
        {
            // 'using var' disposes the JsonDocument at the end of each loop iteration.
            using var doc = await ReceiveAsync(socket, token).ConfigureAwait(false);
            if (doc == null) return;
            HandleMessage(doc.RootElement);
        }
    }

    // ---- Applying messages to the working books ----

    private void HandleMessage(JsonElement root)
    {
        // Subscription failures come back as {"success":false,"error":"..."}.
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown" : "unknown";
            RaiseStatus("Kraken error: " + error);
            return;
        }

        // Ignore everything except book messages (heartbeats, status, subscribe acks, ...).
        if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "book") return;

        bool isSnapshot = root.GetProperty("type").GetString() == "snapshot";

        foreach (var item in root.GetProperty("data").EnumerateArray())
        {
            string symbol = item.GetProperty("symbol").GetString()!;
            if (!_local.TryGetValue(symbol, out var book)) continue;

            var bids = ParseLevels(item.GetProperty("bids"));
            var asks = ParseLevels(item.GetProperty("asks"));

            // A snapshot REPLACES the book; an update CHANGES part of it.
            if (isSnapshot) book.ApplySnapshot(bids, asks);
            else book.ApplyUpdate(bids, asks);

            // Publish: swap a finished, immutable snapshot into the shared dictionary. Null means
            // one side of the book is empty right now, so there is nothing useful to publish.
            var snapshot = book.ToSnapshot();
            if (snapshot != null)
            {
                _books[symbol] = snapshot;
                Interlocked.Increment(ref _updateCount);
            }
        }
    }

    // Reads Kraken's [{"price":..., "qty":...}, ...] into our own BookLevel type. This is the
    // only place that knows Kraken's JSON field names.
    private static List<BookLevel> ParseLevels(JsonElement levels)
    {
        var result = new List<BookLevel>();
        foreach (var level in levels.EnumerateArray())
        {
            result.Add(new BookLevel(level.GetProperty("price").GetDouble(), level.GetProperty("qty").GetDouble()));
        }
        return result;
    }

    // ---- Small WebSocket helpers ----

    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);

    // One message can arrive split across several WebSocket frames (the instrument list is large),
    // so keep reading until EndOfMessage. Returns null if the server closed the connection.
    // The caller must dispose the returned JsonDocument.
    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length));
    }

    private void RaiseStatus(string text) => StatusChanged?.Invoke(this, text);
}
