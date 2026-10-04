---
name: threading-review
description: Reviews C# changes in this repo against its threading and data-flow rules (the feed thread never touches the UI, data crosses threads only as immutable snapshots, the UI polls at frame rate). Use after editing the market data feeds, the hand-drawn controls, the timers in MainWindow, or anything that shares state between threads, and when asked to check thread safety or to diagnose a UI freeze or a cross-thread exception.
---

# Threading review

This app receives market data far faster than a screen can show it. Its whole design depends on a few
rules about which thread may touch what. Review the change against them and report violations. Do not
edit code unless the user asks.

## The rules

1. **The feed thread never touches the UI.** Nothing running on the receive loop or a background
   thread may use a `System.Windows` type (controls, `Dispatcher`, `DispatcherTimer`, `ObservableCollection`
   that is bound to a control). WPF rejects cross-thread access.
2. **No `Dispatcher.BeginInvoke` or `Invoke` per market data update.** At thousands of updates per second
   it queues UI work faster than the UI thread can drain it. The only intended use is for rare events,
   such as `StatusChanged`.
3. **Data crosses threads only as immutable snapshots**, published by replacing the entry in the
   `ConcurrentDictionary`. `OrderBookSnapshot` and its levels must stay immutable: no mutable
   properties, no `List<T>` exposed where `IReadOnlyList<T>` is declared, no mutation after publish.
4. **UI-owned state stays on the UI thread.** `_quotes`, the `PriceHistory` instances, and every
   control. They have no locks on purpose, so a background thread touching them is a bug.
5. **The UI pulls on a timer.** Market data reaches the screen only from the frame timer in
   `MainWindow`. Do not add event handlers that push each update into the UI.
6. **Feed-thread-only state stays on the feed thread.** `LocalOrderBook` and the fake feed's working
   arrays are not thread-safe. After startup only the producer may touch them.
7. **Random numbers:** background threads use `Random.Shared`. A shared `new Random()` is not
   thread-safe.
8. **Async hygiene:** feed classes use `ConfigureAwait(false)` on awaits (they have no UI to return to).
   UI code does not, and it never blocks on a `Task` with `.Result` or `.Wait()`, which can deadlock.
9. **Cleanup:** anything started with `Task.Run` or `new Thread` must observe the cancellation token or
   be a background thread, so closing the window actually stops the app.

Known, intentional exceptions: `StatusChanged` is raised on a background thread and the subscriber hops
to the UI thread with `Dispatcher.BeginInvoke`; the fake feed's constructor runs on the UI thread before
its thread starts.

## How to review

1. **Scope.** Use `git diff HEAD` and `git status --short` to find what changed, or review the files the
   user names.
2. **Search for risk markers** in the changed files: `Dispatcher`, `Task.Run`, `new Thread`, `.Result`,
   `.Wait(`, `lock`, `ConfigureAwait`, `Random`, `ConcurrentDictionary`, `ObservableCollection`.
3. **For each changed method, decide which thread it runs on:**
   - UI thread: constructors, `DispatcherTimer` ticks, event handlers, and the code after an `await`
     in UI code.
   - Feed thread: the receive loop, `Run`, `ReadLoopAsync`, anything started by `Task.Run` or
     `new Thread`, and continuations after `ConfigureAwait(false)`.
4. **Check each rule** against what that method touches. Follow shared fields, not just the method
   in front of you.

## Report

List each violation as: **file:line**, the rule it breaks, a concrete failure scenario (what the user
would see: an exception, a frozen window, a corrupted book), and the smallest fix.

If you find nothing, say "No violations found" and list what you checked, so the user knows how far
the review went. Say plainly what you could not verify (for example, behavior that depends on timing).
