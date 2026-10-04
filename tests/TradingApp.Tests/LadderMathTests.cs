using TradingApp;

namespace TradingApp.Tests;

// Tests for the arithmetic behind "which price and side did the user click?". An off-by-one here
// would log an order at the wrong price, so each rule is pinned down, including the boundaries.
public class LadderMathTests
{
    // ---- Which tick is on the top row? ----

    [Fact]
    public void TopTick_PutsTheMarketInTheMiddleOfTheRows()
    {
        // Best bid at tick 1000, best ask at 1001 (a one-tick spread), 20 rows: the midpoint is tick
        // 1000 (integer division rounds down), and half the rows (10) sit above it.
        Assert.Equal(1010, LadderMath.TopTick(bestBidTick: 1000, bestAskTick: 1001, rows: 20));
    }

    [Fact]
    public void TopTick_MovesUpAndDownWithTheMarket()
    {
        long before = LadderMath.TopTick(1000, 1001, 20);
        long after = LadderMath.TopTick(1005, 1006, 20);

        Assert.Equal(5, after - before);   // the market rose 5 ticks, so the ladder scrolled 5 ticks
    }

    // ---- Grouping (several ticks per row) for wide, sparse markets ----

    [Theory]
    [InlineData(1, 25, 1)]      // tight spread: one tick per row, as normal
    [InlineData(6, 25, 1)]      // spread*4 = 24 still fits in 25 rows
    [InlineData(38, 25, 10)]    // the DOGE case: needs 152 ticks of room; 25 rows x 10 = 250
    [InlineData(38, 60, 5)]     // taller ladder, so a finer grouping is enough (60 x 5 = 300)
    [InlineData(1000, 25, 200)]
    public void RowTicksFor_PicksTheSmallestNiceGroupingThatFitsTheSpread(long spreadTicks, int rows, int expected)
    {
        Assert.Equal(expected, LadderMath.RowTicksFor(spreadTicks, rows));
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 25)]
    [InlineData(37, 31)]
    [InlineData(500, 12)]
    public void RowTicksFor_AlwaysFitsFourSpreadsOnScreen_AndIsAOneTwoFiveStep(long spreadTicks, int rows)
    {
        int rowTicks = LadderMath.RowTicksFor(spreadTicks, rows);

        Assert.True((long)rowTicks * rows >= spreadTicks * 4);
        int leadingDigit = int.Parse(rowTicks.ToString()[0].ToString());
        Assert.Contains(leadingDigit, new[] { 1, 2, 5 });   // 1, 2, 5, 10, 20, 50, ...
    }

    [Theory]
    [InlineData(1239, 10, 1230)]
    [InlineData(1230, 10, 1230)]
    [InlineData(1240, 10, 1240)]
    [InlineData(7, 1, 7)]       // grouping of 1 changes nothing
    public void BucketOf_RoundsDownToAMultipleOfTheRowSize(long tick, int rowTicks, long expectedBucket)
    {
        Assert.Equal(expectedBucket, LadderMath.BucketOf(tick, rowTicks));
    }

    [Fact]
    public void TopTick_WithGrouping_IsAlignedToABucketBoundary()
    {
        // Mid is tick 1237; with 10 ticks per row it belongs to bucket 1230, and half of 20 rows (10)
        // sit above it, 10 ticks each: 1230 + 10*10 = 1330.
        long top = LadderMath.TopTick(bestBidTick: 1236, bestAskTick: 1238, rows: 20, rowTicks: 10);

        Assert.Equal(1330, top);
        Assert.Equal(0, top % 10);   // so rows keep the same edges as the market drifts
    }

    [Fact]
    public void TickAtRow_WithGrouping_StepsByTheRowSize()
    {
        Assert.Equal(1330, LadderMath.TickAtRow(topTick: 1330, row: 0, rowTicks: 10));
        Assert.Equal(1320, LadderMath.TickAtRow(topTick: 1330, row: 1, rowTicks: 10));
        Assert.Equal(1230, LadderMath.TickAtRow(topTick: 1330, row: 10, rowTicks: 10));
    }

    [Fact]
    public void WithGrouping_BestBidAndAskLandOnScreen_WhereOneTickPerRowWouldMissThem()
    {
        // Reproduces the DOGE/USD screenshot: best bid and ask 38 ticks apart, 25 rows.
        long bid = 928515, ask = 928553;
        int rows = 25;

        // One tick per row: the top row is mid+12 and the bottom is mid-12. Both best prices are
        // 19 ticks from the middle, so neither is visible.
        long topUngrouped = LadderMath.TopTick(bid, ask, rows);
        long bottomUngrouped = LadderMath.TickAtRow(topUngrouped, rows - 1);
        Assert.False(bid >= bottomUngrouped && ask <= topUngrouped);

        // With automatic grouping both fit.
        int rowTicks = LadderMath.RowTicksFor(ask - bid, rows);
        long top = LadderMath.TopTick(bid, ask, rows, rowTicks);
        long bottom = LadderMath.TickAtRow(top, rows - 1, rowTicks);
        Assert.True(LadderMath.BucketOf(bid, rowTicks) >= bottom);
        Assert.True(LadderMath.BucketOf(ask, rowTicks) <= top);
    }

    // ---- Row to tick to price ----

    [Theory]
    [InlineData(0, 1010)]    // the top row is the HIGHEST price
    [InlineData(1, 1009)]    // each row down is one tick lower
    [InlineData(10, 1000)]
    [InlineData(19, 991)]
    public void TickAtRow_CountsDownFromTheTop(int row, long expectedTick)
    {
        Assert.Equal(expectedTick, LadderMath.TickAtRow(topTick: 1010, row));
    }

    [Fact]
    public void PriceAtTick_RemovesFloatingPointDust()
    {
        // 12345 * 0.01 is 123.45000000000002 as a raw double. The order must say exactly 123.45.
        Assert.Equal(123.45, LadderMath.PriceAtTick(12345, 0.01));
    }

    [Theory]
    [InlineData(845477, 0.1, 84547.7)]            // BTC-style: one decimal
    [InlineData(928347, 0.0000001, 0.0928347)]     // DOGE/USD on Kraken: 7 decimals. A bug here once rounded this to 0.092835
    [InlineData(9283975, 0.00000001, 0.09283975)]  // an even tinier tick
    [InlineData(268785, 0.01, 2687.85)]
    public void PriceAtTick_ConvertsTicksBackToTheExactPrice(long tick, double tickSize, double expectedPrice)
    {
        Assert.Equal(expectedPrice, LadderMath.PriceAtTick(tick, tickSize));
    }

    [Theory]
    [InlineData(1, 0)]            // a tick of 1 -> whole numbers
    [InlineData(0.1, 1)]
    [InlineData(0.01, 2)]
    [InlineData(0.00001, 5)]      // XRP/USD
    [InlineData(0.0000001, 7)]    // DOGE/USD
    public void DecimalsForTick_MatchesTheTickSize(double tickSize, int expectedDecimals)
    {
        Assert.Equal(expectedDecimals, LadderMath.DecimalsForTick(tickSize));
    }

    // ---- Which row was clicked? ----

    [Theory]
    [InlineData(0, 0)]        // very top of the first row
    [InlineData(19.9, 0)]     // just inside the first row
    [InlineData(20, 1)]       // exactly on the boundary belongs to the NEXT row
    [InlineData(399.9, 19)]   // bottom of the last row (20 rows of height 20 = 400px)
    public void RowAtY_FindsTheRowUnderThePointer(double y, int expectedRow)
    {
        Assert.Equal(expectedRow, LadderMath.RowAtY(y, rowHeight: 20, rowCount: 20));
    }

    [Theory]
    [InlineData(-1)]      // above the control
    [InlineData(400)]     // exactly past the last row
    [InlineData(1000)]    // the empty space below the rows
    public void RowAtY_IsNullOutsideTheDrawnRows(double y)
    {
        Assert.Null(LadderMath.RowAtY(y, rowHeight: 20, rowCount: 20));
    }

    // ---- Which side was clicked? ----

    [Theory]
    [InlineData(0, Side.Buy)]       // far left
    [InlineData(299, Side.Buy)]     // last pixel of the bid column (30% of 1000px)
    [InlineData(700, Side.Sell)]    // first pixel of the ask column (starts at 70%)
    [InlineData(999, Side.Sell)]    // far right
    public void SideAtX_LeftColumnBuysAndRightColumnSells(double x, Side expectedSide)
    {
        Assert.Equal(expectedSide, LadderMath.SideAtX(x, width: 1000));
    }

    [Theory]
    [InlineData(300)]   // first pixel of the price column
    [InlineData(500)]   // middle
    [InlineData(699)]   // last pixel of the price column
    public void SideAtX_IsNullInTheMiddlePriceColumn(double x)
    {
        Assert.Null(LadderMath.SideAtX(x, width: 1000));
    }

    [Fact]
    public void SideAtX_ScalesWithTheControlWidth()
    {
        // Same relative positions at a different width: 30% of 500px is 150px.
        Assert.Equal(Side.Buy, LadderMath.SideAtX(149, width: 500));
        Assert.Null(LadderMath.SideAtX(150, width: 500));
        Assert.Equal(Side.Sell, LadderMath.SideAtX(350, width: 500));
    }
}
