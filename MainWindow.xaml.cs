using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TradingApp;

public partial class MainWindow : Window
{
    // =====================================================================
    // How everything fits together:
    //
    //   KrakenMarketDataSource (WebSocket loop on a background thread)
    //        |  publishes immutable OrderBookSnapshots
    //        v
    //   IMarketDataSource.GetLatestBook(symbol)       <- the only thing the UI knows about
    //        ^
    //        |  polled ~60 times a second by _frameTimer
    //   MainWindow (UI thread)
    //        |-- updates watchlist rows (mutating Quote.Price)
    //        |-- every 100 ms, records each market's mid price into its PriceHistory
    //        |        '-- PriceChartControl draws the selected market's history
    //        '-- hands the selected symbol's book to the LadderControl
    //                 |
    //                 '-- raises PriceClicked ---> OnLadderClicked ---> practice-orders log
    // =====================================================================

    // --- Fields: state only. No statements here. ---

    // Which feed to use. By default the real one (live prices from Kraken, needs internet).
    // Set the environment variable TRADINGAPP_FEED=fake to use made-up prices instead: handy
    // offline, and for demos, because a fake market moves enough to show off the chart.
    private static readonly bool UseFakeFeed =
        string.Equals(Environment.GetEnvironmentVariable("TRADINGAPP_FEED"), "fake", StringComparison.OrdinalIgnoreCase);

    // We hold the INTERFACE type, so nothing below knows or cares which feed this is.
    private readonly IMarketDataSource _source =
        UseFakeFeed ? new FakeMarketDataSource() : new KrakenMarketDataSource();

    // UI-owned. Only the UI thread may touch this (it feeds the DataGrid).
    private readonly ObservableCollection<Quote> _quotes = new();

    // Price history, one per market. Owned by the UI thread (no locking needed): the frame timer
    // writes to it and the chart reads from it, both on the UI thread.
    //
    // Retention vs. performance: 5 minutes sampled 10 times a second is 3,000 points per market,
    // about 72 KB each. That is tiny, and drawing 3,000 points a frame is cheap. Because we SAMPLE
    // at a fixed rate instead of recording every update, memory and drawing cost stay the same
    // however fast the feed is. Raise these two numbers if you want a longer or finer chart.
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<string, PriceHistory> _history = new();
    private DateTime _lastSample = DateTime.MinValue;

    // One timer, one job: ~60 times a second, copy the latest data onto the screen.
    // DispatcherTimer ticks on the UI thread.
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // A second, slow timer, only for the small "developer stats" line.
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Which symbol the chart and ladder are showing. Follows the selected watchlist row.
    private string _selectedSymbol = "";
    private string _rangeLabel = "2 min";

    // The last snapshot we wrote into the ladder's text labels, so we only rebuild the text
    // when the book actually changed.
    private OrderBookSnapshot? _summarizedBook;

    // Counters for the developer stats (UI-thread only, so plain fields are fine).
    private long _lastProduced;
    private int _frames;
    private int _lastFrames;

    // Colours for the "+1.23 (0.01%)" text above the chart.
    private static readonly Brush UpTextBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84));
    private static readonly Brush DownTextBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x39, 0x43));

    // --- Constructor: setup code runs here, after InitializeComponent() has built the XAML. ---
    public MainWindow()
    {
        InitializeComponent();

        WatchlistGrid.ItemsSource = _quotes;

        // Don't claim the prices are real when they aren't.
        if (UseFakeFeed)
        {
            BannerText.Text = "The prices are SIMULATED by a built-in fake feed (for offline use and demos), not real market data. " +
                              "This app never places real orders and no money is involved. Nothing here is financial advice.";
        }

        // Selecting a watchlist row chooses which market the chart and ladder show.
        // 'SelectedItem is Quote quote' checks the type AND gives you a typed variable in one
        // step (like narrowing with `instanceof` in TS).
        WatchlistGrid.SelectionChanged += (sender, e) =>
        {
            if (WatchlistGrid.SelectedItem is Quote quote)
            {
                _selectedSymbol = quote.Symbol;
                Chart.History = _history.GetValueOrDefault(quote.Symbol);   // null if none yet
                ChartTitle.Text = $"Live Price Chart: {quote.Symbol}";
                SummaryLast.Text = "--";
                SummaryChange.Text = "";
                SummaryHighLow.Text = "";
                UpdateChartSummary();
            }
        };

        // The chart's range and style controls. (We subscribe AFTER InitializeComponent, so the
        // IsChecked="True" defaults in the XAML didn't fire these; the chart's own defaults of
        // 2 minutes and Line already match.)
        Range1.Checked += (sender, e) => SetRange(60, "1 min");
        Range2.Checked += (sender, e) => SetRange(120, "2 min");
        Range5.Checked += (sender, e) => SetRange(300, "5 min");
        ModeLine.Checked += (sender, e) => Chart.Mode = ChartMode.Line;
        ModeCandles.Checked += (sender, e) => Chart.Mode = ChartMode.Candles;

        // Step 8: subscribe to the ladder's click event.
        Ladder.PriceClicked += OnLadderClicked;

        // The feed raises StatusChanged on a BACKGROUND thread, and StatusText is a UI object,
        // so hop to the UI thread first. This is step 5's lesson applied. It's fine to use
        // BeginInvoke here because status changes are rare (see IMarketDataSource).
        _source.StatusChanged += (sender, text) => Dispatcher.BeginInvoke(() => StatusText.Text = text);

        // --- Frame timer: the ONLY place market data reaches the screen. ---
        // Safe to start before the feed does: with no rows yet, the loop below does nothing.
        _frameTimer.Tick += (sender, e) =>
        {
            DateTime now = DateTime.UtcNow;

            // Is it time to record a history sample? (Every 100 ms, not every frame.)
            bool sampleDue = now - _lastSample >= SampleInterval;
            if (sampleDue) _lastSample = now;

            // Watchlist: copy the latest mid price into the existing rows (mutate, don't
            // replace, so the user's selection survives). On sample ticks, also record it.
            foreach (var quote in _quotes)
            {
                var book = _source.GetLatestBook(quote.Symbol);
                if (book == null) continue;

                quote.Price = book.Mid;

                if (sampleDue && _history.TryGetValue(quote.Symbol, out var history))
                {
                    history.Add(now, book.Mid);
                }
            }

            // Ladder: hand it the latest snapshot. If nothing changed since the last frame, it's
            // the same object and the ladder skips the repaint.
            if (_selectedSymbol != "")
            {
                var ladderBook = _source.GetLatestBook(_selectedSymbol);
                Ladder.Book = ladderBook;
                UpdateLadderLabels(ladderBook);
            }

            if (sampleDue) UpdateChartSummary();

            // Ask the chart to repaint EVERY frame. Its time axis is tied to the clock, so even
            // with no new data it needs to slide left a little each frame to look smooth.
            Chart.InvalidateVisual();

            _frames++;
        };
        _frameTimer.Start();

        // --- Developer stats, once a second ---
        _statsTimer.Tick += (sender, e) =>
        {
            long produced = _source.UpdateCount;
            StatsText.Text = $"For developers: {produced - _lastProduced} book updates received/s, " +
                             $"{_frames - _lastFrames} screen refreshes/s, " +
                             $"chart drawing {Chart.LastRenderMilliseconds:F2} ms/frame";
            _lastProduced = produced;
            _lastFrames = _frames;
        };
        _statsTimer.Start();

        // Stop the feed when the window closes.
        Closed += (sender, e) => _source.Dispose();

        // Start the feed once the window is on screen. 'Loaded' fires after the first layout.
        // The handler is 'async' so it can 'await' the network without freezing the window.
        // (async void is normally avoided, but it's the standard shape for event handlers.)
        Loaded += async (sender, e) => await StartFeedAsync();
    }

    // Starts the feed, then builds the watchlist from whatever instruments it reports.
    //
    // THE KEY ASYNC IDEA: while 'await _source.StartAsync()' waits on the network, the UI
    // thread is FREE: the window keeps painting and responding. When the work finishes, this
    // method resumes on the UI thread (WPF remembers where the await started), so it is safe
    // to touch _quotes and controls afterwards. Same mental model as JS: await resumes on the
    // main thread.
    private async Task StartFeedAsync()
    {
        try
        {
            await _source.StartAsync();
        }
        catch (Exception ex)
        {
            // No network, firewall, Kraken down, region blocked...
            StatusText.Text = "Feed failed to start: " + ex.Message;
            return;
        }

        foreach (var instrument in _source.GetInstruments())
        {
            _history[instrument.Symbol] = new PriceHistory(HistoryRetention);

            // Price 0 until the first book arrives; the frame timer fills it in.
            _quotes.Add(new Quote(instrument.Symbol, 0));
        }
        WatchlistGrid.SelectedIndex = 0;   // triggers SelectionChanged, which sets _selectedSymbol
    }

    private void SetRange(double seconds, string label)
    {
        Chart.WindowSeconds = seconds;
        _rangeLabel = label;
        UpdateChartSummary();
    }

    // Rewrites the one line of live numbers above the chart: latest price, change over the visible
    // window (green or red), and the window's high and low.
    private void UpdateChartSummary()
    {
        if (!_history.TryGetValue(_selectedSymbol, out var history)) return;
        if (history.Latest is not PricePoint latest) return;

        DateTime from = DateTime.UtcNow - TimeSpan.FromSeconds(Chart.WindowSeconds);
        if (!history.TryGetRange(from, out double min, out double max)) return;

        double start = history.Points[history.FirstIndexAtOrAfter(from)].Price;
        double change = latest.Price - start;
        double percent = start != 0 ? change / start * 100 : 0;
        string format = Chart.PriceFormat;

        SummaryLast.Text = latest.Price.ToString(format);
        SummaryChange.Text = $"{(change >= 0 ? "+" : "-")}{Math.Abs(change).ToString(format)} ({percent:+0.000;-0.000;0.000}%) over {_rangeLabel}";
        SummaryChange.Foreground = change >= 0 ? UpTextBrush : DownTextBrush;
        SummaryHighLow.Text = $"    High {max.ToString(format)}   Low {min.ToString(format)}";
    }

    // Rewrites the order book's title and one-line summary, but only when the book changed
    // (comparing references is free; rebuilding strings 60 times a second is not).
    private void UpdateLadderLabels(OrderBookSnapshot? book)
    {
        if (book == null || ReferenceEquals(book, _summarizedBook)) return;
        _summarizedBook = book;

        string format = book.PriceFormat;
        Chart.PriceFormat = format;   // the chart shows prices with the same decimals as the ladder

        LadderTitle.Text = $"Order Book: {book.Symbol}";
        LadderSummary.Text =
            $"Best price to SELL at (highest bid): {book.BestBid.ToString(format)}\n" +
            $"Best price to BUY at (lowest ask): {book.BestAsk.ToString(format)}\n" +
            $"Spread: {book.Spread.ToString(format)}";

        // When the market is sparse the ladder groups prices together so there is something to
        // see; say so, otherwise the price labels would look like they skip numbers.
        // (This reads the step from the ladder's previous frame. That is one book update stale,
        // which is invisible in practice because the text is rewritten on every book change.)
        if (Ladder.RowPriceStep > book.TickSize * 1.5)
        {
            LadderSummary.Text += $"\nEach row groups prices together: 1 row = {Ladder.RowPriceStep.ToString(format)}";
        }
    }

    // STEP 8: the listener. This is a normal method, not a lambda, because it's big enough to
    // deserve a name. It runs on the UI thread, because the click that raised the event did.
    // For now it just writes a log line. In a real app this is where you'd forward to an
    // order-entry service instead (see LadderClickEventArgs.cs).
    private void OnLadderClicked(object? sender, LadderClickEventArgs e)
    {
        // The widths below ({e.Side,-4} = left-aligned in 4 characters, etc.) must match the
        // column headings in MainWindow.xaml. Quantity is fixed at 1 for now.
        // 0.########## prints only the decimals that are needed (84547.7, 0.0000001, ...).
        string line = $"{DateTime.Now:HH:mm:ss}  {e.Side,-4}  {1,-3}  {e.Symbol,-9}  {e.Price:0.##########}";
        BlotterList.Items.Add(line);
        BlotterList.ScrollIntoView(line);
    }
}
