using System.Collections.Concurrent;

namespace TradingApp;

// A simulated IMarketDataSource (random-walk prices), for offline use, demos and load testing.
//
// A background thread repeatedly builds whole order books and overwrites the latest one per
// symbol in a ConcurrentDictionary. The UI polls that through GetLatestBook() and never deals
// with the thread directly.
public sealed class FakeMarketDataSource : IMarketDataSource
{
    private const int Depth = 10;               // levels per side
    private const int UpdatesPerBatch = 200;    // updates before each 1 ms sleep; raise to simulate load
    private const int MaxDriftTicks = 300;      // keeps prices from wandering off the screen

    private readonly Instrument[] _instruments =
    {
        new("AAPL", 0.01), new("MSFT", 0.01), new("GOOG", 0.01), new("AMZN", 0.01), new("TSLA", 0.01),
    };

    // SHARED between threads: the latest finished snapshot per symbol.
    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _books = new();

    // PRODUCER-THREAD-ONLY state (after StartAsync), indexed like _instruments. Prices are in
    // whole ticks: integers avoid floating-point drift (123.45 stays exactly 12345 ticks).
    private readonly long[] _midTicks;
    private readonly long[] _startTicks;
    private readonly long[] _sequence;

    private long _updateCount;
    private CancellationTokenSource? _cts;   // signals the thread to stop; set by Dispose()
    private Thread? _thread;

    public FakeMarketDataSource()
    {
        _midTicks = new long[_instruments.Length];
        _startTicks = new long[_instruments.Length];
        _sequence = new long[_instruments.Length];

        // Pick a random starting price per instrument and publish an initial book, so that
        // GetLatestBook() returns data immediately, even before StartAsync() is called.
        for (int i = 0; i < _instruments.Length; i++)
        {
            double startPrice = 100 + Random.Shared.Next(0, 100);
            _startTicks[i] = (long)Math.Round(startPrice / _instruments[i].TickSize);
            _midTicks[i] = _startTicks[i];
            _books[_instruments[i].Symbol] = BuildBook(i);
        }
    }

    public long UpdateCount => Interlocked.Read(ref _updateCount);

    public IReadOnlyList<Instrument> GetInstruments() => _instruments;

    public OrderBookSnapshot? GetLatestBook(string symbol) => _books.TryGetValue(symbol, out var book) ? book : null;

    // Raised only once, at startup.
    public event EventHandler<string>? StatusChanged;

    // Starts the producer thread. There is nothing to connect to, so this returns an
    // already-completed Task.
    public Task StartAsync()
    {
        if (_thread != null) return Task.CompletedTask;   // already running

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "FakeMarketData" };
        _thread.Start();

        StatusChanged?.Invoke(this, "Fake feed running");
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Cancel();   // the loop notices and exits

    // Runs on the background thread. Never touches any UI type.
    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            for (int n = 0; n < UpdatesPerBatch; n++)
            {
                // Random.Shared is thread-safe; a single 'new Random()' shared across threads is not.
                int i = Random.Shared.Next(_instruments.Length);
                int step = Random.Shared.Next(-1, 2);   // -1, 0, or +1 tick

                // If the price has drifted too far from where it started, force a step back.
                long drift = _midTicks[i] - _startTicks[i];
                if (drift > MaxDriftTicks) step = -1;
                else if (drift < -MaxDriftTicks) step = 1;

                _midTicks[i] += step;

                // Publish: replacing the dictionary entry is atomic. The UI sees either
                // the old complete snapshot or the new complete snapshot, never a mix.
                _books[_instruments[i].Symbol] = BuildBook(i);
                Interlocked.Increment(ref _updateCount);
            }
            Thread.Sleep(1);   // give the CPU a break; sleeping is fine off the UI thread
        }
    }

    // Builds a complete, immutable book around the current mid. Spread is always one tick:
    // best bid is at midTick, best ask is at midTick + 1.
    private OrderBookSnapshot BuildBook(int i)
    {
        var instrument = _instruments[i];
        long mid = _midTicks[i];
        var bids = new BookLevel[Depth];
        var asks = new BookLevel[Depth];

        for (int k = 0; k < Depth; k++)
        {
            bids[k] = new BookLevel((mid - k) * instrument.TickSize, Random.Shared.Next(1, 500));
            asks[k] = new BookLevel((mid + 1 + k) * instrument.TickSize, Random.Shared.Next(1, 500));
        }

        return new OrderBookSnapshot(instrument.Symbol, instrument.TickSize, bids, asks, ++_sequence[i]);
    }
}
