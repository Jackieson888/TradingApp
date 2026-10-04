namespace TradingApp;

// Everything the UI needs from a market data feed. MainWindow only ever talks to this
// interface, so FakeMarketDataSource and KrakenMarketDataSource are interchangeable (chosen at
// startup by the TRADINGAPP_FEED environment variable).
//
// Design choices:
//   * High-rate data (order books) is POLLED: the UI calls GetLatestBook() at its own frame rate.
//     An event per book update would mean thousands of UI-thread calls a second, more than the
//     UI thread can keep up with.
//   * Rare, low-rate news (connection status) is an EVENT: see StatusChanged.
//
// IDisposable means "this object holds resources that need cleanup". Dispose() stops the feed's
// connection and background work.
public interface IMarketDataSource : IDisposable
{
    // Connects and starts producing data. The returned Task completes once the instrument list is
    // known, so the UI can build the watchlist; book data arrives after that. Throws if it can't
    // connect.
    Task StartAsync();

    // The instruments this feed provides. Empty until StartAsync() has completed.
    IReadOnlyList<Instrument> GetInstruments();

    // Latest published book for a symbol, or null if none has arrived yet. Safe to call from any
    // thread, as often as you like. Returns the same object until the book changes, so callers can
    // compare references (ReferenceEquals) to cheaply tell whether anything is new.
    OrderBookSnapshot? GetLatestBook(string symbol);

    // Total book updates published so far. Diagnostics only (the developer stats line).
    long UpdateCount { get; }

    // Human-readable status changes ("Connecting...", "Reconnecting..."). Raised on a BACKGROUND
    // thread, so a UI subscriber must hop to the UI thread itself (Dispatcher.BeginInvoke). That
    // is acceptable here because it fires rarely, not thousands of times a second.
    event EventHandler<string>? StatusChanged;
}
