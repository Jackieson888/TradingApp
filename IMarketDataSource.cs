namespace TradingApp;

// STEP 6: "What does the UI need from the backend?" written down as an interface.
//
// An interface is the same idea as a TS `interface`: a list of members with no
// implementation. The UI only ever talks to IMarketDataSource, never to a concrete class.
// That is why we could swap FakeMarketDataSource for KrakenMarketDataSource by changing ONE
// line in MainWindow.
//
// The questions from the step, and the answers baked into this interface:
//
//   * A list of instruments?            YES. GetInstruments(). Valid once StartAsync() has
//                                       finished. A real feed has to ask the exchange first.
//   * The latest book for an instrument? YES. GetLatestBook(symbol). The UI POLLS this at its
//                                       own frame rate (step 5's design).
//   * An event when something changes?   NOT for book updates. At thousands of updates a
//                                       second an event would put us right back in step 5's
//                                       "20,000 queued UI calls" problem. But for RARE things
//                                       an event is exactly right: see StatusChanged below.
//   * A method the UI polls?             YES, for the high-rate data. See GetLatestBook.
//
// IDisposable is the .NET "I own something that needs cleanup" interface. Here: the feed's
// connection and background work. Dispose() stops them.
public interface IMarketDataSource : IDisposable
{
    // Connect and begin producing data. Returns when the instrument list is known, so the UI
    // can build the watchlist. Book data arrives after that. Throws if it can't connect.
    //
    // 'Task' is C#'s Promise: `await source.StartAsync()` works like `await` in JS.
    Task StartAsync();

    IReadOnlyList<Instrument> GetInstruments();

    // Latest published book for a symbol, or null if none has arrived yet. Safe to call from
    // any thread, as often as you like. Returns the same snapshot object until something
    // changes, so callers can compare references to cheaply tell if anything is new.
    OrderBookSnapshot? GetLatestBook(string symbol);

    // Total book updates published so far. Diagnostics only: used for the "produced/s" stat.
    long UpdateCount { get; }

    // RARE, human-readable status changes ("Connecting...", "Connected", "Reconnecting...").
    // Raised on a BACKGROUND thread. The subscriber must hop to the UI thread itself
    // (Dispatcher.BeginInvoke). That's fine here because it fires a few times per hour, not a
    // few thousand times per second.
    event EventHandler<string>? StatusChanged;
}
