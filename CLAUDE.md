# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A read-only WPF (.NET 10, `net10.0-windows`) practice trading screen: price chart, order-book ladder, watchlist. It never places real orders. The audience is beginner traders, so every panel carries plain-English headers, descriptions and tooltips. Keep that when adding UI.

The owner is new to C#/WPF and comes from JavaScript/TypeScript. Code comments are deliberately explanatory (they say *why*, often with beginner-level context). Match that density and tone when editing.

## Commands (PowerShell, from repo root)

```powershell
dotnet run                                   # live Kraken feed (needs internet)
$env:TRADINGAPP_FEED = "fake"; dotnet run    # simulated offline feed, much busier, good for chart work
dotnet build TradingApp.csproj
dotnet test tests/TradingApp.Tests
dotnet test tests/TradingApp.Tests --filter "FullyQualifiedName~LadderMathTests"          # one class
dotnet test tests/TradingApp.Tests --filter "FullyQualifiedName~LadderMathTests.SomeTest" # one test
```

- A running copy of the app locks `bin\Debug\...\TradingApp.exe`, so builds and tests fail with MSB3021/MSB3027. Ask the user to close it; never kill `TradingApp` by name (they may have their own copy open). To compile while it is running, build to another folder: `dotnet build TradingApp.csproj -o $env:TEMP\tradingapp_verify`.
- The app project sits at the repo root, and `TradingApp.csproj` explicitly excludes `tests\**` from compilation. Any new folder of non-app code needs the same treatment.
- Project skills: `/verify-app [fake|live]` (compile + tests + smoke-launch), and `threading-review` (run after touching feeds, controls, timers or cross-thread state).

## Architecture: the UI pulls, the feed never pushes

This is the central design rule. Most of the code exists to uphold it.

1. A feed (`KrakenMarketDataSource` over WebSocket, or `FakeMarketDataSource` random walk) runs on a background thread. It keeps a private, non-thread-safe `LocalOrderBook` per market.
2. After each change it publishes an **immutable** `OrderBookSnapshot` into a `ConcurrentDictionary` by replacing the entry atomically.
3. `MainWindow`'s `_frameTimer` (`DispatcherTimer`, 16 ms, UI thread) is the **only** path for market data to reach the screen. Each tick it calls `IMarketDataSource.GetLatestBook`, updates `Quote` rows, samples mid prices into each market's `PriceHistory` every 100 ms, hands the selected book to `LadderControl`, and invalidates `PriceChartControl` (every frame, because the time axis is clock-driven).
4. `LadderControl.PriceClicked` → `MainWindow.OnLadderClicked` appends to the practice-orders log.

Rules that follow from this (full list and rationale in [.claude/skills/threading-review/SKILL.md](.claude/skills/threading-review/SKILL.md)):
- Feed/background code never touches `System.Windows` types, and never calls `Dispatcher.BeginInvoke` per update. The only allowed hop is the rare `StatusChanged` event.
- Snapshots must stay truly immutable (records, `IReadOnlyList`, no post-publish mutation). Tests check that later updates never change an earlier snapshot.
- UI-owned state (`_quotes`, `PriceHistory` instances, controls) has no locks on purpose. Touch it only on the UI thread.
- Feed classes use `ConfigureAwait(false)` and `Random.Shared`. UI code never blocks on `.Result` or `.Wait()`.
- `IMarketDataSource` is the only thing the UI knows about the feed. Both feeds must stay interchangeable behind it.

## Other conventions

- **Prices are integer ticks inside `LocalOrderBook`**, so float dust doesn't create duplicate levels. Round displayed and clicked prices to the market's own tick size, never a fixed decimal count. Fixed 6-decimal rounding caused a real DOGE/USD bug.
- **Chart and ladder are hand-drawn in `OnRender`**, not built from per-cell elements or `DataGrid`. Keep them that way for performance. Only the small watchlist uses a `DataGrid`.
- **Keep testable logic out of the UI.** `PriceHistory`, `LocalOrderBook` and `LadderMath` have no WPF dependencies and are what the xUnit tests cover. Put new non-trivial arithmetic (hit-testing, grouping, aggregation) in a class like these and test it there.
- History is sampled at a fixed rate (5 min × 10 Hz ≈ 3,000 points per market) so memory and draw cost don't scale with feed speed.

## Known gaps (per README)

Kraken book checksums are not verified. There is no trade data, no positions or P&L, and history lives only in memory.
