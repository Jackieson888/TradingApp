using System.Collections.Concurrent;

namespace TradingApp;

// A fake implementation of IMarketDataSource, so the UI can be built and demoed without a feed.
//
// It is a background producer thread that publishes whole order books: it overwrites the
// "latest" state in a ConcurrentDictionary, and the UI polls it. This class is the ONLY place
// that knows a thread exists. MainWindow just calls GetLatestBook().
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

    // PRODUCER-THREAD-ONLY state (after Start): the working prices, in whole ticks.
    // Using integers for price avoids floating-point drift (123.45 stays exactly 12345 ticks).
    private readonly long[] _midTicks;
    private readonly long[] _startTicks;
    private readonly long[] _sequence;

    private long _updateCount;
    private CancellationTokenSource? _cts;   // a "please stop" flag the thread checks
    private Thread? _thread;

    public FakeMarketDataSource()
    {
        _midTicks = new long[_instruments.Length];
        _startTicks = new long[_instruments.Length];
        _sequence = new long[_instruments.Length];

        // Seed a starting price per instrument and publish an initial book, so that
        // GetLatestBook() works immediately, before Start() is ever called.
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

    // Never raised by the fake except once at startup. Declared because the interface requires it.
    public event EventHandler<string>? StatusChanged;

    // Nothing to wait for with a fake, so this returns an already-finished Task.
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
                // Random.Shared is the thread-safe Random (a plain 'new Random()' shared
                // across threads is not safe).
                int i = Random.Shared.Next(_instruments.Length);
                int step = Random.Shared.Next(-1, 2);   // -1, 0, or +1 tick

                // Gentle pull back toward the starting price.
                long drift = _midTicks[i] - _startTicks[i];
                if (drift > MaxDriftTicks) step = -1;
                else if (drift < -MaxDriftTicks) step = 1;

                _midTicks[i] += step;

                // Publish: replacing the dictionary entry is atomic. The UI sees either
                // the old complete snapshot or the new complete snapshot, never a mix.
                _books[_instruments[i].Symbol] = BuildBook(i);
                Interlocked.Increment(ref _updateCount);
            }
            Thread.Sleep(1);   // yield the CPU; fine to sleep, this is not the UI thread
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
