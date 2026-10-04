using TradingApp;

namespace TradingApp.Tests;

// Tests for the order-book bookkeeping that sits between the exchange's raw messages and the UI.
// This is the easiest place to get a live feed subtly wrong (a level that never gets removed, bids
// sorted the wrong way round, a price that drifts by a rounding error), so it gets the most tests.
public class LocalOrderBookTests
{
    // Tick size 0.1 means valid prices are 100.0, 100.1, 100.2 ... Keeps the numbers readable.
    private static readonly Instrument Market = new("TEST/USD", 0.1);

    private static LocalOrderBook NewBook(int depth = 10) => new(Market, depth);

    // Shorthand for building a list of levels: Levels((100.0, 5), (100.1, 3)).
    private static List<BookLevel> Levels(params (double Price, double Size)[] levels) =>
        levels.Select(l => new BookLevel(l.Price, l.Size)).ToList();

    private static readonly List<BookLevel> NoLevels = new();

    // ---- Snapshots ----

    [Fact]
    public void ToSnapshot_IsNullWhenTheBookIsEmpty()
    {
        Assert.Null(NewBook().ToSnapshot());
    }

    [Fact]
    public void ToSnapshot_IsNullWhenOneSideIsEmpty()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 5)), NoLevels);

        Assert.Null(book.ToSnapshot());   // a spread needs a bid AND an ask
    }

    [Fact]
    public void Snapshot_SortsBidsHighToLowAndAsksLowToHigh_WhateverOrderTheyArriveIn()
    {
        var book = NewBook();

        book.ApplySnapshot(
            bids: Levels((100.0, 1), (100.2, 2), (100.1, 3)),
            asks: Levels((100.5, 1), (100.3, 2), (100.4, 3)));

        var snapshot = book.ToSnapshot()!;
        Assert.Equal(new[] { 100.2, 100.1, 100.0 }, snapshot.Bids.Select(b => b.Price).ToArray(), new PriceComparer());
        Assert.Equal(new[] { 100.3, 100.4, 100.5 }, snapshot.Asks.Select(a => a.Price).ToArray(), new PriceComparer());
        Assert.Equal(100.2, snapshot.BestBid, precision: 6);
        Assert.Equal(100.3, snapshot.BestAsk, precision: 6);
        Assert.Equal(0.1, snapshot.Spread, precision: 6);
        Assert.Equal(100.25, snapshot.Mid, precision: 6);
    }

    [Fact]
    public void Snapshot_CarriesTheSymbolAndTickSize()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 1)), Levels((100.1, 1)));

        var snapshot = book.ToSnapshot()!;

        Assert.Equal("TEST/USD", snapshot.Symbol);
        Assert.Equal(0.1, snapshot.TickSize);
    }

    [Fact]
    public void ApplySnapshot_ReplacesWhatWasThereBefore()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 1), (99.9, 1)), Levels((100.1, 1), (100.2, 1)));

        // A fresh snapshot (e.g. after a reconnect) must wipe the old levels, not merge with them.
        book.ApplySnapshot(Levels((50.0, 9)), Levels((50.1, 9)));

        var snapshot = book.ToSnapshot()!;
        Assert.Single(snapshot.Bids);
        Assert.Single(snapshot.Asks);
        Assert.Equal(50.0, snapshot.BestBid, precision: 6);
    }

    // ---- Updates ----

    [Fact]
    public void Update_ChangesTheSizeOfAnExistingLevel()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 5)), Levels((100.1, 5)));

        book.ApplyUpdate(Levels((100.0, 7)), NoLevels);

        var snapshot = book.ToSnapshot()!;
        Assert.Equal(7, snapshot.Bids[0].Size);
        Assert.Equal(5, snapshot.Asks[0].Size);   // the other side is untouched
    }

    [Fact]
    public void Update_WithSizeZeroRemovesTheLevel()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 5), (99.9, 4)), Levels((100.1, 5)));

        book.ApplyUpdate(Levels((100.0, 0)), NoLevels);   // size 0 = "this level is gone"

        var snapshot = book.ToSnapshot()!;
        Assert.Single(snapshot.Bids);
        Assert.Equal(99.9, snapshot.BestBid, precision: 6);
    }

    [Fact]
    public void Update_RemovingALevelThatDoesNotExistIsHarmless()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 5)), Levels((100.1, 5)));

        var exception = Record.Exception(() => book.ApplyUpdate(Levels((42.0, 0)), NoLevels));

        Assert.Null(exception);
        Assert.Single(book.ToSnapshot()!.Bids);
    }

    [Fact]
    public void Update_CanAddANewBestBid()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 5)), Levels((100.5, 5)));

        book.ApplyUpdate(Levels((100.3, 2)), NoLevels);

        var snapshot = book.ToSnapshot()!;
        Assert.Equal(100.3, snapshot.BestBid, precision: 6);
        Assert.Equal(2, snapshot.Bids.Count);
    }

    // ---- Depth ----

    [Fact]
    public void Depth_KeepsOnlyTheBestLevelsOnEachSide()
    {
        var book = NewBook(depth: 3);

        book.ApplySnapshot(
            bids: Levels((100.0, 1), (99.9, 1), (99.8, 1), (99.7, 1), (99.6, 1)),
            asks: Levels((100.1, 1), (100.2, 1), (100.3, 1), (100.4, 1), (100.5, 1)));

        var snapshot = book.ToSnapshot()!;
        Assert.Equal(new[] { 100.0, 99.9, 99.8 }, snapshot.Bids.Select(b => b.Price).ToArray(), new PriceComparer());   // the HIGHEST bids
        Assert.Equal(new[] { 100.1, 100.2, 100.3 }, snapshot.Asks.Select(a => a.Price).ToArray(), new PriceComparer()); // the LOWEST asks
    }

    [Fact]
    public void Depth_IsRestoredAfterAnUpdateAddsABetterLevel()
    {
        var book = NewBook(depth: 2);
        book.ApplySnapshot(Levels((99.9, 1), (99.8, 1)), Levels((100.1, 1), (100.2, 1)));

        // A new, better bid at 100.0 arrives. The book is limited to 2 levels, so the worst bid
        // (99.8) must fall off to make room.
        book.ApplyUpdate(Levels((100.0, 1)), NoLevels);

        var snapshot = book.ToSnapshot()!;
        Assert.Equal(new[] { 100.0, 99.9 }, snapshot.Bids.Select(b => b.Price).ToArray(), new PriceComparer());
    }

    [Fact]
    public void Constructor_RejectsDepthBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalOrderBook(Market, 0));
    }

    // ---- Number handling ----

    [Fact]
    public void PricesThatDifferOnlyByFloatingPointDust_AreTheSameLevel()
    {
        // In floating point, 0.1 + 0.2 is 0.30000000000000004, not 0.3. The book keys levels by whole
        // ticks, so both spellings must land on the SAME level instead of creating a duplicate.
        var book = NewBook();
        book.ApplySnapshot(Levels((0.3, 1)), Levels((0.5, 1)));

        book.ApplyUpdate(Levels((0.1 + 0.2, 2)), NoLevels);

        var snapshot = book.ToSnapshot()!;
        Assert.Single(snapshot.Bids);
        Assert.Equal(2, snapshot.Bids[0].Size);
    }

    // ---- Snapshots are immutable and numbered ----

    [Fact]
    public void ToSnapshot_IncrementsTheSequenceEachTime()
    {
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 1)), Levels((100.1, 1)));

        long first = book.ToSnapshot()!.Sequence;
        long second = book.ToSnapshot()!.Sequence;

        Assert.Equal(first + 1, second);
    }

    [Fact]
    public void AnEarlierSnapshot_IsNotAffectedByLaterUpdates()
    {
        // This is the property the whole threading design relies on: the UI can hold a snapshot
        // while the feed thread keeps updating the book, and the UI's copy never changes.
        var book = NewBook();
        book.ApplySnapshot(Levels((100.0, 5)), Levels((100.1, 5)));
        var before = book.ToSnapshot()!;

        book.ApplyUpdate(Levels((100.0, 999)), NoLevels);

        Assert.Equal(5, before.Bids[0].Size);
        Assert.Equal(999, book.ToSnapshot()!.Bids[0].Size);
    }

    // Compares doubles with a small tolerance, so tests don't fail on 100.30000000000001 vs 100.3.
    private sealed class PriceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6;
        public int GetHashCode(double value) => 0;
    }
}
