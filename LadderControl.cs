using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TradingApp;

// The order-book ladder, drawn by hand in OnRender.
//
// Why not a DataGrid?
//   * A DataGrid creates real WPF elements (rows, cells, text blocks, borders) for every row, and
//     each goes through layout, styling and binding. A 50-row ladder updated 60 times a second
//     would mean tens of thousands of element updates per second.
//   * Here ONE element records ONE list of drawing commands ("draw this rectangle, this text")
//     and WPF paints it. No per-row elements, bindings or layout passes.
//   * A ladder isn't really a table: the price axis scrolls with the market, bars vary in width,
//     and clicks need pixel-exact mapping to prices.
//
// FrameworkElement is the lightest WPF base class that takes part in layout and receives mouse
// input. It has no appearance of its own; everything visible comes from OnRender.
public class LadderControl : FrameworkElement
{
    // ---- Look and layout constants ----
    private const double RowHeight = 20;
    private const double TextSize = 12;
    // Column split comes from LadderMath so the drawing and the click math always agree.
    private const double BidColumnFraction = LadderMath.BidColumnFraction;
    private const double PriceColumnFraction = LadderMath.PriceColumnFraction;

    // Brushes and pens are created once and frozen. Freeze() makes a WPF resource immutable, so
    // WPF can skip change tracking on it. Worth doing for anything reused every frame.
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

    // The book to show next. Set through the Book property by MainWindow's frame timer.
    private OrderBookSnapshot? _book;

    // What was actually drawn on the most recent render. The click handler uses these, NOT
    // _book, so a click always means what the user SAW, even if a newer book has arrived since.
    private OrderBookSnapshot? _renderedBook;
    private long _renderedTopTick;
    private int _renderedRows;
    private int _renderedRowTicks = 1;

    // How much price one row covered on the last render. Equals the tick size normally; larger
    // when the ladder groups several ticks into each row (wide spreads).
    public double RowPriceStep { get; private set; }

    public LadderControl()
    {
        ClipToBounds = true;   // don't let long numbers draw outside the control
    }

    // Assigning a new book requests a repaint. InvalidateVisual() does not draw immediately; it
    // marks the element as needing a render, and WPF calls OnRender later on the UI thread.
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

    // Raised when the user clicks a bid or ask cell. Subscribe with 'Ladder.PriceClicked += handler'.
    // The '?' is because the event is null until something subscribes.
    public event EventHandler<LadderClickEventArgs>? PriceClicked;

    // ---- Drawing ----

    // WPF calls this whenever the element needs painting: on first display, after
    // InvalidateVisual(), and on resize (see OnRenderSizeChanged). Each dc.DrawXxx call adds one
    // command to a drawing that WPF keeps and paints.
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        // Fill the whole control with a background. This also matters for click-to-trade: WPF
        // only delivers mouse clicks to pixels that were actually painted, so without it clicks
        // on "empty" areas would fall straight through.
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

        // Work in whole ticks (integers) so row-to-price math never suffers floating-point drift.
        double tick = book.TickSize;
        long bestBidTick = (long)Math.Round(book.BestBid / tick);
        long bestAskTick = (long)Math.Round(book.BestAsk / tick);

        // How many ticks does each row cover? Usually 1, but a wide spread needs coarser rows so
        // the best bid and ask are actually on screen (see LadderMath.RowTicksFor).
        int rowTicks = LadderMath.RowTicksFor(Math.Max(0, bestAskTick - bestBidTick), rows);
        RowPriceStep = rowTicks * tick;

        // Row 0 is the TOP of the screen and shows the HIGHEST price. The market is centered
        // vertically, so as the mid moves, topTick moves and the whole ladder scrolls.
        long topTick = LadderMath.TopTick(bestBidTick, bestAskTick, rows, rowTicks);

        // Remember exactly what this frame showed, for hit-testing in OnMouseLeftButtonDown.
        _renderedBook = book;
        _renderedTopTick = topTick;
        _renderedRows = rows;
        _renderedRowTicks = rowTicks;

        // Total size per row. When rowTicks > 1, several book levels land in one row and their
        // sizes are added. (Two small dictionaries per render is cheap at this scale; they could
        // be reused if it ever mattered.)
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

        string priceFormat = book.PriceFormat;   // as many decimals as the tick size needs

        // The price column gets its own slightly lighter background.
        dc.DrawRectangle(PriceColumnBrush, null, new Rect(bidWidth, 0, priceWidth, rows * RowHeight));

        for (int row = 0; row < rows; row++)
        {
            long rowTick = LadderMath.TickAtRow(topTick, row, rowTicks);
            double y = row * RowHeight;
            double price = rowTick * tick;

            // Bid side: the bar grows LEFTWARD from the price column; size text sits next to the price.
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

            // Price label. The rows holding the best bid and best ask ("inside" prices) are highlighted.
            bool isInside = rowTick == bestBidRow || rowTick == bestAskRow;
            DrawText(dc, price.ToString(priceFormat, CultureInfo.InvariantCulture),
                     isInside ? HighlightBrush : TextBrush, pixelsPerDip, bidWidth, priceWidth, y, TextAlignment.Center);

            dc.DrawLine(GridPen, new Point(0, y + RowHeight), new Point(width, y + RowHeight));
        }
    }

    // Sizes range from whole coins (DOGE) to tiny fractions (0.00005 BTC), so pick decimals by
    // magnitude to keep the text short enough to fit its column.
    private static string FormatSize(double size) =>
        size >= 100 ? size.ToString("F0", CultureInfo.InvariantCulture)
        : size >= 1 ? size.ToString("F2", CultureInfo.InvariantCulture)
        : size.ToString("F4", CultureInfo.InvariantCulture);

    // Draws text aligned within a column and vertically centered in a row. WPF text drawing takes
    // three steps: build a FormattedText (text + font + brush), use its measured size to position
    // it, then draw it. The 'switch' expression picks a value based on 'align'.
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

    // WPF calls this override on every left-button press over the control. It turns the mouse
    // position into a (price, side) using what was last DRAWN, then raises PriceClicked. The
    // arithmetic itself lives in LadderMath, which is unit tested.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var book = _renderedBook;
        if (book == null) return;

        Point point = e.GetPosition(this);   // relative to this control's top-left corner

        // Which row? Null means the click landed outside the drawn rows.
        int? row = LadderMath.RowAtY(point.Y, RowHeight, _renderedRows);
        if (row == null) return;

        // Which price? Row 0 is the top row; each row down is _renderedRowTicks ticks lower.
        long clickedTick = LadderMath.TickAtRow(_renderedTopTick, row.Value, _renderedRowTicks);
        double price = LadderMath.PriceAtTick(clickedTick, book.TickSize);

        // Which side? Left (bids) = Buy, right (asks) = Sell, middle (prices) = ignore.
        Side? side = LadderMath.SideAtX(point.X, ActualWidth);
        if (side == null) return;

        // '?.Invoke' calls every subscriber, or does nothing if there are none.
        PriceClicked?.Invoke(this, new LadderClickEventArgs(book.Symbol, price, side.Value));
        e.Handled = true;   // we dealt with this click; stop it bubbling up to parent elements
    }
}
