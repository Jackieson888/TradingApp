using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TradingApp;

// The price ladder, drawn by hand.
//
// "Why not just use a DataGrid for this?"
//
//   * A DataGrid builds real WPF elements (a row, cells, text blocks, borders) for every row,
//     and each of those goes through layout, styling and binding. A 50-row ladder redrawn 60
//     times a second means tens of thousands of element updates per second.
//   * Here, ONE element owns ONE drawing. OnRender emits a list of "draw this rectangle, draw
//     this text" commands, and WPF keeps that list and paints it. No per-row elements, no
//     per-cell bindings, no layout pass per row. (It's the same idea as a <canvas> in JS,
//     except WPF retains the drawing commands instead of leaving you pixels.)
//   * A ladder isn't a table: the price axis scrolls with the market, size bars have varying
//     widths, and you want pixel-exact control of what a click means. A grid fights you on all
//     of that.
//
// FrameworkElement is the lightest WPF base class that can take part in layout and receive
// mouse input. It has no look of its own. If we don't draw anything, it's invisible.
public class LadderControl : FrameworkElement
{
    // ---- Look and layout constants ----
    private const double RowHeight = 20;
    private const double TextSize = 12;
    // Column split lives in LadderMath so the click math and the drawing always agree.
    private const double BidColumnFraction = LadderMath.BidColumnFraction;       // left: bid sizes (click = Buy)
    private const double PriceColumnFraction = LadderMath.PriceColumnFraction;   // middle: prices (click = nothing)
    // the remaining right 30%: ask sizes (click = Sell)

    // Brushes and pens are created once and Frozen. A frozen WPF resource is immutable, which
    // lets WPF skip change tracking and share it across threads. Always do this for brushes
    // you reuse every frame.
    private static readonly Typeface Face = new("Consolas");
    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(0x14, 0x17, 0x1c));
    private static readonly Brush PriceColumnBrush = Frozen(Color.FromRgb(0x1d, 0x21, 0x28));
    private static readonly Brush BidBarBrush = Frozen(Color.FromRgb(0x2a, 0x5d, 0x9f));
    private static readonly Brush AskBarBrush = Frozen(Color.FromRgb(0xa3, 0x3a, 0x3a));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xd0, 0xd4, 0xdc));
    private static readonly Brush HighlightBrush = Frozen(Color.FromRgb(0xff, 0xd5, 0x4a));
    private static readonly Pen GridPen = MakePen(Color.FromRgb(0x2a, 0x2f, 0x38));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Color color)
    {
        var pen = new Pen(Frozen(color), 1);
        pen.Freeze();
        return pen;
    }

    // ---- State ----

    // The book we've been TOLD to show. Set by MainWindow's frame timer.
    private OrderBookSnapshot? _book;

    // What we actually drew on the most recent frame. The click handler uses these, NOT _book,
    // so a click always means what the user SAW, even if a newer book arrived a millisecond
    // after the last render.
    private OrderBookSnapshot? _renderedBook;
    private long _renderedTopTick;
    private int _renderedRows;
    private int _renderedRowTicks = 1;

    // How much price one row covers right now, as of the last drawn frame. Equals the tick size
    // normally; larger when the ladder is grouping several ticks into each row (wide spreads).
    public double RowPriceStep { get; private set; }

    public LadderControl()
    {
        // Don't draw outside our own bounds (long numbers could otherwise spill over).
        ClipToBounds = true;
    }

    // Assigning a new book asks WPF to repaint. WPF calls OnRender later, at a convenient
    // time on the UI thread. InvalidateVisual() does NOT draw right now. It just marks the
    // element dirty, so calling it many times in a row costs almost nothing extra.
    public OrderBookSnapshot? Book
    {
        get => _book;
        set
        {
            if (ReferenceEquals(_book, value)) return;   // same snapshot: nothing to redraw
            _book = value;
            InvalidateVisual();
        }
    }

    // The event. "event EventHandler<T>?" is a list of subscribers; the '?' means it's
    // null when nobody has subscribed. Subscribers use `ladder.PriceClicked += handler;`
    // (like addEventListener).
    public event EventHandler<LadderClickEventArgs>? PriceClicked;

    // ---- Drawing ----

    // WPF calls this whenever the element needs painting: first display, after
    // InvalidateVisual(), and (via the override below) on resize. 'dc' is the recorder: every
    // dc.DrawXxx call adds one command to the retained drawing.
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        // Fill the whole control with a background. Besides looking right, this matters for
        // click-to-trade: WPF only delivers mouse clicks to pixels that were actually painted. Without
        // this rectangle, clicks on "empty" areas would fall straight through.
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var book = _book;
        int rows = (int)(height / RowHeight);
        if (book == null || rows < 1 || width <= 0)
        {
            _renderedBook = null;
            return;
        }

        double bidWidth = width * BidColumnFraction;
        double priceWidth = width * PriceColumnFraction;
        double askWidth = width - bidWidth - priceWidth;
        double askLeft = bidWidth + priceWidth;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;   // required by FormattedText

        // Work in whole ticks (integers) so row -> price math never suffers float drift.
        double tick = book.TickSize;
        long bestBidTick = (long)Math.Round(book.BestBid / tick);
        long bestAskTick = (long)Math.Round(book.BestAsk / tick);

        // How many ticks does each row cover? Usually 1, but a wide spread needs coarser rows so
        // the best bid and ask are actually on screen (see LadderMath.RowTicksFor).
        int rowTicks = LadderMath.RowTicksFor(Math.Max(0, bestAskTick - bestBidTick), rows);
        RowPriceStep = rowTicks * tick;

        // Row 0 is the TOP of the screen and shows the HIGHEST price. Centre the market
        // vertically. As the mid moves, topTick moves and the whole ladder scrolls.
        long topTick = LadderMath.TopTick(bestBidTick, bestAskTick, rows, rowTicks);

        // Remember exactly what this frame showed, for hit-testing in OnMouseLeftButtonDown.
        _renderedBook = book;
        _renderedTopTick = topTick;
        _renderedRows = rows;
        _renderedRowTicks = rowTicks;

        // Total size per row bucket. When rowTicks > 1, several book levels land in one row and
        // their sizes are added. This allocates two tiny dictionaries per frame; it's fine at
        // this scale. (Optimization for later: reuse them.)
        var bidSizes = new Dictionary<long, double>();
        var askSizes = new Dictionary<long, double>();
        foreach (var level in book.Bids)
        {
            long bucket = LadderMath.BucketOf((long)Math.Round(level.Price / tick), rowTicks);
            bidSizes[bucket] = bidSizes.GetValueOrDefault(bucket) + level.Size;
        }
        foreach (var level in book.Asks)
        {
            long bucket = LadderMath.BucketOf((long)Math.Round(level.Price / tick), rowTicks);
            askSizes[bucket] = askSizes.GetValueOrDefault(bucket) + level.Size;
        }

        // Bars are scaled against the biggest total among the rows currently ON SCREEN, not the
        // whole book. Otherwise one huge level far from the market would shrink every visible
        // bar to a sliver.
        long bottomTick = LadderMath.TickAtRow(topTick, rows - 1, rowTicks);
        double maxSize = 0;
        foreach (var (bucket, size) in bidSizes)
            if (bucket <= topTick && bucket >= bottomTick) maxSize = Math.Max(maxSize, size);
        foreach (var (bucket, size) in askSizes)
            if (bucket <= topTick && bucket >= bottomTick) maxSize = Math.Max(maxSize, size);

        if (maxSize <= 0) maxSize = 1;   // avoid dividing by zero for an all-empty book

        // The rows that contain the best bid and the best ask get highlighted.
        long bestBidRow = LadderMath.BucketOf(bestBidTick, rowTicks);
        long bestAskRow = LadderMath.BucketOf(bestAskTick, rowTicks);

        // Show as many decimals as the tick size needs (see OrderBookSnapshot.PriceFormat).
        string priceFormat = book.PriceFormat;

        // The price column gets its own slightly lighter background.
        dc.DrawRectangle(PriceColumnBrush, null, new Rect(bidWidth, 0, priceWidth, rows * RowHeight));

        for (int row = 0; row < rows; row++)
        {
            long rowTick = LadderMath.TickAtRow(topTick, row, rowTicks);
            double y = row * RowHeight;
            double price = rowTick * tick;

            // Bid side: bar grows LEFTWARD from the price column; size text sits next to the price.
            if (bidSizes.TryGetValue(rowTick, out double bidSize))
            {
                double barWidth = bidWidth * bidSize / maxSize;
                dc.DrawRectangle(BidBarBrush, null, new Rect(bidWidth - barWidth, y + 1, barWidth, RowHeight - 2));
                DrawText(dc, FormatSize(bidSize), TextBrush, pixelsPerDip, 0, bidWidth, y, TextAlignment.Right);
            }

            // Ask side: bar grows RIGHTWARD from the price column.
            if (askSizes.TryGetValue(rowTick, out double askSize))
            {
                double barWidth = askWidth * askSize / maxSize;
                dc.DrawRectangle(AskBarBrush, null, new Rect(askLeft, y + 1, barWidth, RowHeight - 2));
                DrawText(dc, FormatSize(askSize), TextBrush, pixelsPerDip, askLeft, askWidth, y, TextAlignment.Left);
            }

            // Price label; the best bid and best ask are highlighted.
            bool isInside = rowTick == bestBidRow || rowTick == bestAskRow;
            DrawText(dc, price.ToString(priceFormat, CultureInfo.InvariantCulture),
                     isInside ? HighlightBrush : TextBrush, pixelsPerDip, bidWidth, priceWidth, y, TextAlignment.Center);

            // Thin line under the row.
            dc.DrawLine(GridPen, new Point(0, y + RowHeight), new Point(width, y + RowHeight));
        }
    }

    // Sizes range from 50 DOGE-coins to 0.00005 BTC, so pick decimals by magnitude to keep the
    // text short enough to fit its column.
    private static string FormatSize(double size) =>
        size >= 100 ? size.ToString("F0", CultureInfo.InvariantCulture)
        : size >= 1 ? size.ToString("F2", CultureInfo.InvariantCulture)
        : size.ToString("F4", CultureInfo.InvariantCulture);

    // Drawing text takes a few steps in WPF: build a FormattedText (text + font + brush), measure
    // it, then draw it at a point. The switch expression below is a compact if/else chain that
    // produces a value (like a TS ternary chain).
    private static void DrawText(DrawingContext dc, string text, Brush brush, double pixelsPerDip,
                                 double columnLeft, double columnWidth, double rowTop, TextAlignment align)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                          Face, TextSize, brush, pixelsPerDip);

        const double padding = 4;
        double x = align switch
        {
            TextAlignment.Left => columnLeft + padding,
            TextAlignment.Right => columnLeft + columnWidth - formatted.Width - padding,
            _ => columnLeft + (columnWidth - formatted.Width) / 2,
        };
        double y = rowTop + (RowHeight - formatted.Height) / 2;

        dc.DrawText(formatted, new Point(x, y));
    }

    // When the control is resized, the number of visible rows changes, so repaint.
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    // ---- Click to trade ----

    // 'protected override' = we're replacing a method the base class (UIElement) already has,
    // and WPF calls it for us on every left-button press over this element.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Use what we last DREW, not whatever _book is now.
        var book = _renderedBook;
        if (book == null) return;

        // Mouse position relative to this control's top-left corner.
        Point point = e.GetPosition(this);

        // The arithmetic lives in LadderMath (which has unit tests). This method only does
        // the UI part: read the mouse position, then announce the result.

        // Which row? Null means the click landed outside the drawn rows.
        int? row = LadderMath.RowAtY(point.Y, RowHeight, _renderedRows);
        if (row == null) return;

        // Which price? Row 0 is the top tick, and each row down is one tick lower.
        long clickedTick = LadderMath.TickAtRow(_renderedTopTick, row.Value, _renderedRowTicks);
        double price = LadderMath.PriceAtTick(clickedTick, book.TickSize);

        // Which side? Decided by the COLUMN: left = bids = Buy, right = asks = Sell.
        // The middle (price) column does nothing.
        Side? side = LadderMath.SideAtX(point.X, ActualWidth);
        if (side == null) return;

        // Announce it. '?.Invoke' calls every subscriber, or does nothing if there are none.
        // The ladder doesn't know or care who is listening or what they'll do.
        PriceClicked?.Invoke(this, new LadderClickEventArgs(book.Symbol, price, side.Value));
        e.Handled = true;   // we dealt with this click; stop it bubbling up to parent elements
    }
}
