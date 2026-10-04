using TradingApp;

namespace TradingApp.Tests;

// Tests for the rolling price history behind the live chart.
//
// How to read these: each [Fact] is one test: a method xUnit runs and reports pass or fail.
// The pattern is Arrange (set things up), Act (do the thing), Assert (check the result).
// PriceHistory never reads the clock (callers pass timestamps in), so every test here is
// deterministic. There is no waiting and no flakiness.
public class PriceHistoryTests
{
    // A fixed starting moment. It is exactly on a 5-second boundary (midnight + 12 hours), which the
    // candle tests rely on. Kind=Utc matches what the app uses.
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PriceHistory NewHistory(double retentionSeconds = 300) =>
        new(TimeSpan.FromSeconds(retentionSeconds));

    // ---- Adding and reading ----

    [Fact]
    public void Add_StoresPointsOldestFirst()
    {
        var history = NewHistory();

        history.Add(T0, 100);
        history.Add(T0.AddSeconds(1), 101);
        history.Add(T0.AddSeconds(2), 102);

        Assert.Equal(3, history.Count);
        Assert.Equal(100, history.Points[0].Price);
        Assert.Equal(102, history.Points[2].Price);
        Assert.Equal(102, history.Latest!.Value.Price);
    }

    [Fact]
    public void Latest_IsNullWhenEmpty()
    {
        Assert.Null(NewHistory().Latest);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Add_RejectsPricesThatAreNotUsableNumbers(double badPrice)
    {
        var history = NewHistory();

        bool added = history.Add(T0, badPrice);

        Assert.False(added);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Add_RejectsTimestampsOlderThanTheNewestPoint()
    {
        var history = NewHistory();
        history.Add(T0.AddSeconds(5), 100);

        bool added = history.Add(T0.AddSeconds(4), 200);

        Assert.False(added);
        Assert.Equal(1, history.Count);
        Assert.Equal(100, history.Latest!.Value.Price);
    }

    [Fact]
    public void Add_AcceptsTheSameTimestampTwice()
    {
        // Two samples in the same instant are fine (only going BACKWARDS in time is rejected).
        var history = NewHistory();
        history.Add(T0, 100);

        bool added = history.Add(T0, 101);

        Assert.True(added);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveRetention()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceHistory(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceHistory(TimeSpan.FromSeconds(-1)));
    }

    // ---- Retention: the memory bound ----

    [Fact]
    public void Add_DropsPointsOlderThanTheRetentionWindow()
    {
        var history = NewHistory(retentionSeconds: 10);

        history.Add(T0, 1);                    // will expire
        history.Add(T0.AddSeconds(5), 2);
        history.Add(T0.AddSeconds(10), 3);
        history.Add(T0.AddSeconds(15), 4);     // now = +15s, so anything before +5s is dropped

        Assert.Equal(3, history.Count);
        Assert.Equal(T0.AddSeconds(5), history.Points[0].Time);   // the point exactly AT the cutoff is kept
        Assert.Equal(4, history.Latest!.Value.Price);
    }

    [Fact]
    public void Retention_KeepsMemoryBoundedOverLongRuns()
    {
        // Ten sample-per-second readings for an hour, with a 5-minute window: the history must
        // never grow past about 5 minutes' worth of points.
        var history = NewHistory(retentionSeconds: 300);

        for (int i = 0; i < 3600 * 10; i++)
        {
            history.Add(T0.AddMilliseconds(i * 100), 100 + i % 7);
        }

        Assert.InRange(history.Count, 2990, 3010);   // ~300 s * 10 per second, give or take the edge
    }

    // ---- Searching ----

    [Fact]
    public void FirstIndexAtOrAfter_FindsTheBoundary()
    {
        var history = NewHistory();
        for (int i = 0; i < 5; i++) history.Add(T0.AddSeconds(i), 100 + i);   // seconds 0..4

        Assert.Equal(0, history.FirstIndexAtOrAfter(T0.AddSeconds(-10)));     // before everything
        Assert.Equal(2, history.FirstIndexAtOrAfter(T0.AddSeconds(2)));       // exactly on a point
        Assert.Equal(3, history.FirstIndexAtOrAfter(T0.AddSeconds(2.5)));     // between two points
        Assert.Equal(5, history.FirstIndexAtOrAfter(T0.AddSeconds(99)));      // after everything = Count
    }

    [Fact]
    public void FirstIndexAtOrAfter_OnEmptyHistoryIsZero()
    {
        Assert.Equal(0, NewHistory().FirstIndexAtOrAfter(T0));
    }

    [Fact]
    public void TryGetRange_ReturnsMinAndMaxFromTheCutoffOnward()
    {
        var history = NewHistory();
        history.Add(T0, 10);
        history.Add(T0.AddSeconds(1), 12);
        history.Add(T0.AddSeconds(2), 8);
        history.Add(T0.AddSeconds(3), 11);

        Assert.True(history.TryGetRange(T0, out double min, out double max));
        Assert.Equal(8, min);
        Assert.Equal(12, max);

        // Starting later ignores earlier points: from +3s only the price 11 remains.
        Assert.True(history.TryGetRange(T0.AddSeconds(3), out min, out max));
        Assert.Equal(11, min);
        Assert.Equal(11, max);
    }

    [Fact]
    public void TryGetRange_IsFalseWhenNoPointsAreInRange()
    {
        var history = NewHistory();
        history.Add(T0, 10);

        Assert.False(history.TryGetRange(T0.AddSeconds(1), out _, out _));
        Assert.False(NewHistory().TryGetRange(T0, out _, out _));
    }

    // ---- Candles ----

    [Fact]
    public void ToCandles_SummarizesEachTimeSliceAsOpenHighLowClose()
    {
        var history = NewHistory();
        // Slice 1 (+0s..+5s): 100, 105, 95, 102 -> open 100, high 105, low 95, close 102
        history.Add(T0.AddSeconds(0), 100);
        history.Add(T0.AddSeconds(1), 105);
        history.Add(T0.AddSeconds(2), 95);
        history.Add(T0.AddSeconds(4), 102);
        // Slice 2 (+5s..+10s): 103, 101 -> open 103, high 103, low 101, close 101
        history.Add(T0.AddSeconds(5), 103);
        history.Add(T0.AddSeconds(9), 101);

        var candles = history.ToCandles(TimeSpan.FromSeconds(5), T0);

        Assert.Equal(2, candles.Count);
        Assert.Equal(new Candle(T0, 100, 105, 95, 102), candles[0]);
        Assert.Equal(new Candle(T0.AddSeconds(5), 103, 103, 101, 101), candles[1]);
    }

    [Fact]
    public void ToCandles_SkipsEmptyTimeSlices()
    {
        var history = NewHistory();
        history.Add(T0.AddSeconds(1), 100);
        history.Add(T0.AddSeconds(16), 110);   // nothing at all between +5s and +15s

        var candles = history.ToCandles(TimeSpan.FromSeconds(5), T0);

        Assert.Equal(2, candles.Count);
        Assert.Equal(T0, candles[0].Start);
        Assert.Equal(T0.AddSeconds(15), candles[1].Start);   // the gap simply has no candles
    }

    [Fact]
    public void ToCandles_AlignsSlicesToTheClockNotToTheFirstPoint()
    {
        // The first point arrives 3 seconds into a slice. The candle must still start at the
        // slice boundary (+0s), so candles don't shift sideways as the window scrolls.
        var history = NewHistory();
        history.Add(T0.AddSeconds(3), 100);
        history.Add(T0.AddSeconds(4), 101);
        history.Add(T0.AddSeconds(6), 102);

        var candles = history.ToCandles(TimeSpan.FromSeconds(5), T0);

        Assert.Equal(T0, candles[0].Start);
        Assert.Equal(T0.AddSeconds(5), candles[1].Start);
    }

    [Fact]
    public void ToCandles_IgnoresPointsBeforeTheFromTime()
    {
        var history = NewHistory();
        history.Add(T0.AddSeconds(1), 100);
        history.Add(T0.AddSeconds(6), 200);

        var candles = history.ToCandles(TimeSpan.FromSeconds(5), T0.AddSeconds(5));

        Assert.Single(candles);
        Assert.Equal(200, candles[0].Open);
    }

    [Fact]
    public void ToCandles_OnEmptyHistoryReturnsNothing()
    {
        Assert.Empty(NewHistory().ToCandles(TimeSpan.FromSeconds(5), T0));
    }

    [Fact]
    public void ToCandles_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewHistory().ToCandles(TimeSpan.Zero, T0));
    }
}
