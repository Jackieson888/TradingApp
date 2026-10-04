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
    //   Kraken or Fake MarketDataSource (background thread)
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
    //                 '-- raises PriceClicked ---> OnLadderClicked ---> order log
    // =====================================================================

    // ---- Fields ----

    // Which feed to use. By default the live Kraken feed (needs internet). Set the environment
    // variable TRADINGAPP_FEED=fake to use simulated prices instead: useful offline, and for
    // demos, because the fake market moves enough to show off the chart.
    private static readonly bool UseFakeFeed =
        string.Equals(Environment.GetEnvironmentVariable("TRADINGAPP_FEED"), "fake", StringComparison.OrdinalIgnoreCase);

    // Typed as the interface, so nothing below depends on which feed this is.
    private readonly IMarketDataSource _source =
        UseFakeFeed ? new FakeMarketDataSource() : new KrakenMarketDataSource();

    // The watchlist rows. ObservableCollection tells the bound DataGrid when rows are added.
    // UI-thread only, because the DataGrid is bound to it.
    private readonly ObservableCollection<Quote> _quotes = new();

    // Price history, one per market. UI-thread only (so no locking): the frame timer writes to
    // it and the chart reads from it.
    //
    // 5 minutes sampled 10 times a second is 3,000 points per market, well under 100 KB each.
    // Because prices are SAMPLED at a fixed rate rather than recorded on every update, memory and
    // drawing cost stay the same however fast the feed is. Change these two values for a longer
    // or finer-grained chart.
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<string, PriceHistory> _history = new();
    private DateTime _lastSample = DateTime.MinValue;

    // ~60 times a second, copies the latest data onto the screen. A DispatcherTimer runs its
    // Tick handler on the UI thread, so the handler can touch controls directly.
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // Once a second, refreshes the "For developers" stats line.
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Which symbol the chart and ladder are showing. Follows the selected watchlist row.
    private string _selectedSymbol = "";
    private string _rangeLabel = "2 min";   // shown in the chart summary ("... over 2 min")

    // The last book written into the ladder's text labels, so the text is only rebuilt when
    // the book actually changes.
    private OrderBookSnapshot? _summarizedBook;

    // Counters for the developer stats (UI-thread only, so plain fields are fine).
    private long _lastProduced;
    private int _frames;
    private int _lastFrames;

    // Colors for the "+1.23 (0.01%)" text above the chart.
    // Frozen (made immutable) like the brushes in the drawn controls, so WPF skips change tracking.
    private static readonly Brush UpTextBrush = Frozen(Color.FromRgb(0x16, 0xC7, 0x84));
    private static readonly Brush DownTextBrush = Frozen(Color.FromRgb(0xEA, 0x39, 0x43));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---- Constructor: wires up events and timers. InitializeComponent() must run first; it
    //      builds the controls declared in MainWindow.xaml. ----
    public MainWindow()
    {
        InitializeComponent();

        WatchlistGrid.ItemsSource = _quotes;

        // The XAML banner says prices are live; reword it when they're simulated.
        if (UseFakeFeed)
        {
            BannerText.Text = "The prices are SIMULATED by a built-in fake feed (for offline use and demos), not real market data. " +
                              "This app never places real orders and no money is involved. Nothing here is financial advice.";
        }

        // Selecting a watchlist row chooses which market the chart and ladder show.
        // 'SelectedItem is Quote quote' checks the type and, if it matches, declares a typed
        // variable 'quote' in one step (pattern matching).
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

        // The chart's range and style radio buttons. These handlers are attached after
        // InitializeComponent, so the IsChecked="True" defaults in the XAML don't trigger them.
        // The chart's own defaults (2 minutes, Line) already match those.
        Range1.Checked += (sender, e) => SetRange(60, "1 min");
        Range2.Checked += (sender, e) => SetRange(120, "2 min");
        Range5.Checked += (sender, e) => SetRange(300, "5 min");
        ModeLine.Checked += (sender, e) => Chart.Mode = ChartMode.Line;
        ModeCandles.Checked += (sender, e) => Chart.Mode = ChartMode.Candles;

        Ladder.PriceClicked += OnLadderClicked;

        // StatusChanged is raised on a BACKGROUND thread, and WPF controls may only be touched
        // from the UI thread, so Dispatcher.BeginInvoke queues the update onto it. That is fine
        // here because status changes are rare (see IMarketDataSource).
        _source.StatusChanged += (sender, text) => Dispatcher.BeginInvoke(() => StatusText.Text = text);

        // ---- Frame timer: the ONLY place market data reaches the screen. ----
        // Safe to start before the feed does: with no watchlist rows yet, it does almost nothing.
        _frameTimer.Tick += (sender, e) =>
        {
            DateTime now = DateTime.UtcNow;

            // Is it time to record a history sample? (Every SampleInterval, not every frame.)
            bool sampleDue = now - _lastSample >= SampleInterval;
            if (sampleDue) _lastSample = now;

            // Watchlist: update each existing row's price in place (replacing the row objects would
            // lose the user's selection). On sample ticks, also record the price in its history.
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

            // Ladder: hand it the latest snapshot. If nothing changed since the last frame it is
            // the same object, and the ladder skips the repaint.
            if (_selectedSymbol != "")
            {
                var ladderBook = _source.GetLatestBook(_selectedSymbol);
                Ladder.Book = ladderBook;
                UpdateLadderLabels(ladderBook);
            }

            if (sampleDue) UpdateChartSummary();

            // Repaint the chart EVERY frame. Its time axis is tied to the clock, so even with no
            // new data it slides left a little each frame.
            Chart.InvalidateVisual();

            _frames++;
        };
        _frameTimer.Start();

        // ---- Developer stats: per-second rates, from the change in each counter since last tick ----
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

        Closed += (sender, e) => _source.Dispose();   // stop the feed when the window closes

        // Start the feed once the window is on screen ('Loaded' fires after the first layout).
        // The lambda is 'async' so it can await the network without freezing the window. An
        // async lambda used as an event handler is 'async void', which is normally avoided but
        // is the standard pattern for event handlers.
        Loaded += async (sender, e) => await StartFeedAsync();
    }

    // Starts the feed, then builds the watchlist from the instruments it reports.
    //
    // While 'await _source.StartAsync()' waits on the network, the UI thread is free: the window
    // keeps painting and responding. Afterwards this method resumes on the UI thread (await
    // returns to the thread it started on unless told otherwise), so it is safe to touch
    // _quotes and controls.
    private async Task StartFeedAsync()
    {
        try
        {
            await _source.StartAsync();
        }
        catch (Exception ex)
        {
            // e.g. no network, firewall, Kraken down, region blocked
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

    // Rewrites the line of live numbers above the chart: latest price, change over the visible
    // window (green or red), and the window's high and low. Does nothing until there is data.
    // ('is not PricePoint latest' unwraps the nullable Latest into 'latest', or returns if null.)
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

    // Rewrites the order book's title and summary, but only when the book has changed
    // (comparing references is nearly free; rebuilding strings 60 times a second is not).
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

        // When the market is sparse the ladder groups several ticks per row. Say so, otherwise
        // the price labels look like they skip numbers. (RowPriceStep is from the ladder's
        // previous render, so it can lag by one book update; not noticeable in practice.)
        if (Ladder.RowPriceStep > book.TickSize * 1.5)
        {
            LadderSummary.Text += $"\nEach row groups prices together: 1 row = {Ladder.RowPriceStep.ToString(format)}";
        }
    }

    // Handles ladder clicks by adding a line to the order log. Runs on the UI thread,
    // because the mouse click that raised the event did. A real app would forward the click to
    // an order-entry service here instead (see LadderClickEventArgs.cs).
    private void OnLadderClicked(object? sender, LadderClickEventArgs e)
    {
        // The column widths ({e.Side,-4} = left-aligned, padded to 4 characters, etc.) must match
        // the headings in MainWindow.xaml. Quantity is fixed at 1.
        // '0.##########' prints only the decimals that are needed (84547.7, 0.0000001, ...).
        string line = $"{DateTime.Now:HH:mm:ss}  {e.Side,-4}  {1,-3}  {e.Symbol,-9}  {e.Price:0.##########}";
        BlotterList.Items.Add(line);
        BlotterList.ScrollIntoView(line);
    }
}
