namespace TradingApp;

// The ladder's layout and click arithmetic, pulled out of LadderControl as plain static
// functions. They need no UI objects, so they can be unit tested. This is where "which price
// and side did the user click?" gets decided, and an off-by-one here would send the wrong order,
// so it deserves tests.
public static class LadderMath
{
    // The ladder is split into three columns by width.
    public const double BidColumnFraction = 0.30;     // left 30%: bid sizes (click = Buy)
    public const double PriceColumnFraction = 0.40;   // middle 40%: prices (click = nothing)
    // the remaining right 30%: ask sizes (click = Sell)

    // GROUPING. Normally each row is one tick. But if the market is sparse (a wide gap between best
    // bid and best ask), a one-tick-per-row ladder shows an empty screen with the interesting part
    // off the top and bottom. So rows can each cover several ticks ('rowTicks'), and the sizes of
    // all the levels inside a row are added together. Trading screens call this "grouping".
    //
    // Chooses the smallest "nice" row size (1, 2, 5, 10, 20, 50, 100 ...) for which the visible rows
    // span at least 4x the spread, so both sides of the market and some room around them fit.
    public static int RowTicksFor(long spreadTicks, int rows)
    {
        long needed = Math.Max(spreadTicks * 4, rows);   // never ask for less than one tick per row

        long magnitude = 1;
        for (int i = 0; i < 9; i++, magnitude *= 10)
        {
            foreach (int multiple in new[] { 1, 2, 5 })
            {
                long candidate = magnitude * multiple;
                if (candidate * rows >= needed) return (int)candidate;
            }
        }
        return 1_000_000_000;
    }

    // The row bucket a tick falls into: rounds DOWN to a multiple of rowTicks. With rowTicks = 10,
    // ticks 1230..1239 are all in the bucket 1230. (Prices are always positive, so plain integer
    // division, which rounds toward zero, is the same as rounding down.)
    public static long BucketOf(long tick, int rowTicks) => tick / rowTicks * rowTicks;

    // The tick shown on the TOP row. We centre the market vertically: half the rows sit above the
    // midpoint, half below. Ticks are whole numbers, so this uses integer division. The result is
    // aligned to a bucket boundary, so rows keep the same edges as the market moves.
    public static long TopTick(long bestBidTick, long bestAskTick, int rows, int rowTicks = 1) =>
        BucketOf((bestBidTick + bestAskTick) / 2, rowTicks) + (long)(rows / 2) * rowTicks;

    // Row 0 is the top of the screen and shows the HIGHEST price; each row down is 'rowTicks' lower.
    public static long TickAtRow(long topTick, int row, int rowTicks = 1) => topTick - (long)row * rowTicks;

    // How many decimal places a price needs to be written exactly for this tick size:
    // tick 0.1 -> 1, tick 0.01 -> 2, tick 0.0000001 -> 7. (Capped at 15, the most a double holds.)
    public static int DecimalsForTick(double tickSize) =>
        Math.Clamp((int)Math.Round(-Math.Log10(tickSize)), 0, 15);

    // Converts a tick number back to a price. The rounding removes floating-point dust:
    // 12345 * 0.01 is 123.45000000000002 in a double, but 123.45 after rounding.
    // It rounds to the tick's own number of decimals. A fixed number (like 6) would silently
    // corrupt prices for markets with finer ticks, e.g. DOGE/USD at 0.0000001.
    public static double PriceAtTick(long tick, double tickSize) =>
        Math.Round(tick * tickSize, DecimalsForTick(tickSize));

    // Which row is at vertical position y? Null if it's outside the drawn rows (e.g. the empty
    // space below the last row, or above the first). 'int?' is a nullable int.
    public static int? RowAtY(double y, double rowHeight, int rowCount)
    {
        if (y < 0) return null;
        int row = (int)(y / rowHeight);
        return row < rowCount ? row : null;
    }

    // Which side does a click at horizontal position x mean? Left column = Buy (you hit a bid),
    // right column = Sell, middle = nothing.
    public static Side? SideAtX(double x, double width)
    {
        double bidEdge = width * BidColumnFraction;
        double askEdge = width * (BidColumnFraction + PriceColumnFraction);

        if (x < bidEdge) return Side.Buy;
        if (x >= askEdge) return Side.Sell;
        return null;
    }
}
