# Trading Practice

A Windows desktop app (WPF, C#, .NET 10) that shows a live trading screen — a scrolling price chart, an order-book ladder and a watchlist — built to be **readable by someone who has never traded**. Every panel explains itself in plain English, and nothing ever places a real order.

![The app running on the simulated feed: candlestick chart with crosshair, watchlist, and order-book ladder](docs/img/screenshot.png)

> **Practice mode only.** The app never places real orders and no money is involved. It is a learning and engineering project, not financial advice.

## Why I built it

I wanted a project that exercises the hard part of a trading UI: **a stream of data arriving far faster than a screen can show it.** A naive WPF app that updates the UI on every message either crashes (touching UI objects from the wrong thread) or freezes (thousands of queued UI calls per second). This project is built around avoiding both, and it keeps the code readable enough to explain, with comments aimed at someone coming back to C# after a break.

## What it does

- **Live price chart** drawn by hand with `OnRender`: line mode (glow + gradient fill) and candlestick mode, 1 / 2 / 5 minute windows, a time axis tied to the clock so labels glide, a price scale that eases rather than jumps, a pulsing last-price marker, and a crosshair with an OHLC readout. History is recorded live while the app runs.
- **Order-book ladder**, also hand-drawn: buyers on the left, sellers on the right, bar length = size, best bid/ask highlighted. When a market is sparse it automatically **groups several price steps per row** so the interesting part stays on screen.
- **Click to trade (practice):** click the blue side of a price to practice a buy, the red side to practice a sell. The click becomes an event; a practice-orders log listens.
- **Real market data** from Kraken's public WebSocket API (BTC, ETH, SOL, XRP, DOGE against USD) — free, no account or API key. A built-in simulated feed works offline.
- **Beginner-first UI:** a header and description on every section, tooltips, a collapsible glossary (bid, ask, spread, tick, candle…), and a banner stating plainly that nothing is real money.

## Demos

**Chart features** — hover crosshair, line ↔ candles, 1 / 2 / 5 minute windows. *Recorded on the **simulated** feed, because real markets were too quiet to show the chart moving.*

![Chart demo on the simulated feed](docs/img/chart-demo-simulated.gif)

**Real market data** — switching between live Kraken markets, each with its own history and order book. Note the status line and the banner. *Recorded on the **live Kraken feed**; the real prices barely moved during the recording, so the charts are mostly steps and flat stretches. The ladder on XRP/USD shows the automatic grouping ("1 row = 0.00002").*

![Switching between live Kraken markets](docs/img/live-markets-kraken.gif)

**Click to trade** — clicking the buyer (blue) and seller (red) sides of the ladder logs practice orders at the clicked price. *Recorded on the **live Kraken feed**.*

![Clicking the order book to log practice orders](docs/img/click-to-trade-kraken.gif)

## How it works

```mermaid
flowchart LR
    K["Kraken WebSocket<br/>public market data"] --> R["Receive loop<br/>background thread"]
    R --> L["LocalOrderBook<br/>applies snapshot + updates"]
    L -- "immutable OrderBookSnapshot" --> S[("ConcurrentDictionary<br/>latest book per market")]
    S -- "polled about 60 times a second" --> U["UI thread<br/>frame timer"]
    U --> W["Watchlist"]
    U --> H["PriceHistory<br/>5 minutes, sampled 10x a second"]
    H --> C["PriceChartControl"]
    U --> D["LadderControl"]
    D -- "PriceClicked event" --> B["Practice orders log"]
```

### The central design decision: the UI pulls, the feed never pushes

The feed thread **never touches the UI**. It keeps its own private copy of each order book, and after every change publishes a finished, **immutable** snapshot into a dictionary (replacing the old one atomically). The UI thread then **polls** that dictionary on a fixed ~60 Hz timer and draws whatever is latest.

Consequences:

- **No cross-thread UI access**, so no dispatcher calls and no locks around the books.
- **UI work is fixed by the timer, not by the feed's speed.** If the feed doubles in speed, the UI does the same amount of work. The alternative — one `Dispatcher.BeginInvoke` per update — queues work on the UI thread faster than it can drain at high rates.
- **The UI can never see a half-updated book**, because it only ever sees finished snapshots.
- Updates that nobody could see anyway (the 500th change to a price inside one frame) are dropped on purpose.

Measured on my machine (your numbers will differ): on the **simulated** feed, about 13,000–17,000 book updates per second arrived while the screen refreshed about 38–40 times per second, and the chart's drawing code cost about 0.5–0.8 ms per frame on the UI thread. The real Kraken feed is much calmer (roughly 60–270 updates per second in my runs). The app shows these figures in a small "For developers" line at the bottom left.

### Other decisions worth knowing about

- **Feed behind an interface.** The UI only knows `IMarketDataSource` (`StartAsync`, `GetInstruments`, `GetLatestBook`, a rare `StatusChanged` event). The simulated and real feeds are interchangeable. High-rate data is polled; only rare things like "reconnecting…" are events.
- **Prices are integers inside the book.** Levels are keyed by whole ticks, so `0.1 + 0.2` and `0.3` land on the same level instead of creating a duplicate.
- **History is sampled, not recorded per update.** Memory and drawing cost stay bounded no matter how fast data arrives (5 minutes × 10 samples/second ≈ 3,000 points per market).
- **Hand-drawn controls instead of a `DataGrid`.** One element recording drawing commands in `OnRender`, instead of thousands of per-cell elements going through layout and binding.
- **Pure logic extracted from the UI so it can be tested:** `PriceHistory`, `LocalOrderBook` and `LadderMath` have no UI dependencies.

## Project layout

| File | What it is |
|---|---|
| [`IMarketDataSource.cs`](IMarketDataSource.cs) | The contract between UI and feed, with the reasoning for each member |
| [`KrakenMarketDataSource.cs`](KrakenMarketDataSource.cs) | Real feed: WebSocket, JSON parsing, reconnects |
| [`FakeMarketDataSource.cs`](FakeMarketDataSource.cs) | Simulated feed (random walk), offline |
| [`LocalOrderBook.cs`](LocalOrderBook.cs) | Applies snapshots and updates, produces immutable snapshots |
| [`OrderBookSnapshot.cs`](OrderBookSnapshot.cs), [`Instrument.cs`](Instrument.cs) | Immutable data types (records) |
| [`PriceHistory.cs`](PriceHistory.cs) | Rolling price window and candle aggregation |
| [`PriceChartControl.cs`](PriceChartControl.cs) | The hand-drawn chart |
| [`LadderControl.cs`](LadderControl.cs), [`LadderMath.cs`](LadderMath.cs) | The hand-drawn order book, and its click/grouping arithmetic |
| [`LadderClickEventArgs.cs`](LadderClickEventArgs.cs) | What a ladder click carries (symbol, price, side) |
| [`MainWindow.xaml`](MainWindow.xaml), [`MainWindow.xaml.cs`](MainWindow.xaml.cs) | Layout, theme, and the wiring between feed and controls |
| [`tests/TradingApp.Tests`](tests/TradingApp.Tests) | Unit tests |

## Running it

**Requirements:** Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download) (it is a WPF app).

```powershell
dotnet run
```

On first start it connects to Kraken and the watchlist fills in. The price chart starts empty and fills as the app runs (history is kept in memory only).

**No internet, or you want a lively chart?** Use the simulated feed:

```powershell
$env:TRADINGAPP_FEED = "fake"
dotnet run
```

The banner at the top changes to say the prices are simulated. (Kraken's public market data is free, but whether it is reachable can depend on your region.)

## Tests

```powershell
dotnet test tests/TradingApp.Tests
```

80+ unit tests (xUnit) cover the logic that is easiest to get subtly wrong:

- **`PriceHistory`** — retention and the memory bound, out-of-order and invalid samples, binary search, candle aggregation and clock alignment.
- **`LocalOrderBook`** — snapshot vs. update, size-0 removal, bid/ask ordering, depth trimming, floating-point-dust prices, and that an earlier snapshot is never changed by later updates (the property the threading design depends on).
- **`LadderMath`** — which row, price and side a click means, including exact boundaries, and the grouping rules for wide markets.

The tests found a real bug while I was building this: clicking DOGE/USD logged `0.092835` instead of `0.0928347`, because prices were rounded to a fixed 6 decimals and that market needs 7. It now rounds to the market's own tick size, with a regression test.

**Not covered:** the WebSocket/JSON parsing and the drawing code. Those would need integration or visual tests, and I verified them by running the app against the live feed and checking screenshots.

## Limitations

- **No real trading.** Clicks only append to a log; quantity is fixed at 1; there are no positions, fills or profit/loss.
- Order books come from the exchange as a **snapshot plus updates**. Kraken includes a checksum with each update for detecting a corrupted local book; **this app does not verify it yet.**
- No trade (executed-price) data yet, so there is no last-trade marker on the ladder.
- Price history exists only while the app is open.
- Windows only, because of WPF.

## Data and acknowledgements

Market data comes from [Kraken's public WebSocket API (v2)](https://docs.kraken.com/api/docs/websocket-v2/book). This project is not affiliated with or endorsed by Kraken.
