namespace TradingApp;

// One price level in the book: "Size contracts are available at Price".
//
// 'readonly record struct' = a record that lives inline (a value type), not on the heap.
// A book has dozens of levels and we rebuild it thousands of times a second, so skipping
// one heap allocation per level matters a little. Behaves like a small immutable object.
//
// Size is a double because real markets trade fractions (0.0005 BTC).
public readonly record struct BookLevel(double Price, double Size);

// The order book for one instrument at one moment in time.
//
// IMMUTABLE ON PURPOSE. Nothing in here can change after construction. That is what makes it
// safe to hand between threads: the producer builds a new snapshot and swaps the reference
// in the shared dictionary, and the UI reads whichever snapshot is there. The UI can never
// see a half-updated book, because it never sees an in-progress one, only finished ones.
// (In JS terms: replace the object, don't mutate it.)
//
// Bids are sorted best-first (highest price first); asks are sorted best-first (lowest first).
public sealed record OrderBookSnapshot(
    string Symbol,
    double TickSize,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    long Sequence)   // increments every time this symbol's book changes; handy for "did it change?"
{
    // Computed properties: no storage, just a getter that runs each time you read them.
    // '=>' after a property name is shorthand for { get { return ...; } }.
    public double BestBid => Bids.Count > 0 ? Bids[0].Price : double.NaN;
    public double BestAsk => Asks.Count > 0 ? Asks[0].Price : double.NaN;
    public double Mid => (BestBid + BestAsk) / 2;
    public double Spread => BestAsk - BestBid;

    // A .NET number format with just enough decimals for this instrument's tick size:
    // tick 0.1 -> "F1", tick 0.0000001 -> "F7". ("F2" means "fixed point, 2 decimals".)
    public string PriceFormat => "F" + LadderMath.DecimalsForTick(TickSize);
}
