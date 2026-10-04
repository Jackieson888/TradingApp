namespace TradingApp;

// The working copy of ONE market's order book, built from a snapshot plus a stream of changes.
// Kept separate from KrakenMarketDataSource so this bookkeeping can be unit tested without a
// network connection.
//
// An exchange feed sends (1) a snapshot ("here is the whole book"), then (2) updates ("level X now
// has size Y"; size 0 means "level X is gone"). This class applies those and can produce an
// immutable OrderBookSnapshot at any moment.
//
// NOT thread-safe: it is owned by a single thread (the feed's receive loop). Other threads only
// ever see the immutable snapshots made by ToSnapshot().
public sealed class LocalOrderBook
{
    private readonly int _depth;

    // Key = price in WHOLE TICKS (an integer), value = size at that price. Integer keys avoid
    // floating-point trouble: 84547.7 always maps to the same key, never "almost" the same one.
    // SortedDictionary keeps its keys in order.
    // Bids sort HIGH to LOW (best bid first), so this one gets a reversed comparer.
    private readonly SortedDictionary<long, double> _bids = new(Comparer<long>.Create((a, b) => b.CompareTo(a)));

    // Asks sort LOW to HIGH (best ask first): the default order.
    private readonly SortedDictionary<long, double> _asks = new();

    private long _sequence;

    public LocalOrderBook(Instrument instrument, int depth)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
        Instrument = instrument;
        _depth = depth;
    }

    public Instrument Instrument { get; }

    // A snapshot REPLACES everything we knew.
    public void ApplySnapshot(IEnumerable<BookLevel> bids, IEnumerable<BookLevel> asks)
    {
        _bids.Clear();
        _asks.Clear();
        ApplyUpdate(bids, asks);
    }

    // An update CHANGES some levels and leaves the rest alone.
    public void ApplyUpdate(IEnumerable<BookLevel> bids, IEnumerable<BookLevel> asks)
    {
        Apply(_bids, bids);
        Apply(_asks, asks);
        Trim(_bids);
        Trim(_asks);
    }

    private void Apply(SortedDictionary<long, double> side, IEnumerable<BookLevel> levels)
    {
        foreach (var level in levels)
        {
            long key = (long)Math.Round(level.Price / Instrument.TickSize);

            if (level.Size == 0) side.Remove(key);   // size 0 means "this level is gone"
            else side[key] = level.Size;
        }
    }

    // Keep only 'depth' levels per side by dropping the worst ones. The worst levels are LAST in
    // each side's sort order (lowest bids, highest asks).
    private void Trim(SortedDictionary<long, double> side)
    {
        while (side.Count > _depth)
            side.Remove(side.Keys.Last());
    }

    // Builds a finished, immutable snapshot of the book as it stands now. Returns null if either
    // side is empty, because a one-sided book has no meaningful spread or midpoint.
    // Each successful call bumps the sequence number. Converting ticks back to prices is rounded
    // to remove floating-point dust (100.30000000000001 -> 100.3).
    public OrderBookSnapshot? ToSnapshot()
    {
        if (_bids.Count == 0 || _asks.Count == 0) return null;

        double tick = Instrument.TickSize;
        var bids = _bids.Select(kv => new BookLevel(Math.Round(kv.Key * tick, 10), kv.Value)).ToArray();
        var asks = _asks.Select(kv => new BookLevel(Math.Round(kv.Key * tick, 10), kv.Value)).ToArray();

        return new OrderBookSnapshot(Instrument.Symbol, tick, bids, asks, ++_sequence);
    }
}
