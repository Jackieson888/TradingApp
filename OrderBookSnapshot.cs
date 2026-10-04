namespace TradingApp;

// One price level in the book: "Size units are available at Price".
//
// 'readonly record struct' is an immutable record stored as a value type (no separate heap
// allocation). Books are rebuilt thousands of times a second, so this saves one allocation per level.
//
// Size is a double because crypto markets trade fractions (e.g. 0.0005 BTC).
public readonly record struct BookLevel(double Price, double Size);

// The order book for one instrument at one moment in time.
//
// IMMUTABLE ON PURPOSE: nothing can change after construction. That is what makes it safe to
// share between threads. The feed thread builds a new snapshot and swaps it into a shared
// dictionary; the UI thread reads whichever snapshot is there. The UI can never see a
// half-updated book, because it only ever sees finished ones.
//
// Bids and asks are both sorted best-first: bids from highest price, asks from lowest.
public sealed record OrderBookSnapshot(
    string Symbol,
    double TickSize,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    long Sequence)   // increases every time this symbol's book changes
{
    // Computed properties: '=>' here is shorthand for a getter, so each value is recalculated
    // whenever it is read. NaN if that side of the book is empty.
    public double BestBid => Bids.Count > 0 ? Bids[0].Price : double.NaN;
    public double BestAsk => Asks.Count > 0 ? Asks[0].Price : double.NaN;
    public double Mid => (BestBid + BestAsk) / 2;
    public double Spread => BestAsk - BestBid;

    // A .NET number format string with just enough decimals for this tick size:
    // tick 0.1 -> "F1", tick 0.0000001 -> "F7". ("F2" means fixed-point with 2 decimals.)
    public string PriceFormat => "F" + LadderMath.DecimalsForTick(TickSize);
}
