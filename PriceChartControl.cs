using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TradingApp;

public enum ChartMode { Line, Candles }

// A live price chart drawn by hand, using the same technique as LadderControl: one element whose
// OnRender records drawing commands. No per-point WPF elements, no bindings.
//
// It shows the last few minutes of one market's prices (from a PriceHistory). The right edge is
// always "now", and the chart scrolls left as time passes.
//
// Animated details, each handled in its own section of Draw():
//   * the time axis is tied to the CLOCK, so labels glide left as time passes
//   * the vertical scale EASES toward its new range instead of jumping
//   * a dot with a pulsing halo marks the latest price, with a price tag on the axis
//   * hovering shows a crosshair and a readout of the price at that point
//
// MainWindow calls InvalidateVisual() every frame (~60 fps), which is what makes it animate.
public class PriceChartControl : FrameworkElement
{
    // ---- Layout constants ----
    private const double LeftPad = 8;
    private const double TopPad = 10;
    private const double RightAxisWidth = 78;     // room for price labels
    private const double BottomAxisHeight = 24;   // room for time labels
    private const int CandleCount = 60;           // candle mode always shows about this many candles

    // ---- Colors. Brushes and pens are created once and frozen (made immutable) so WPF can
    //      skip change tracking on them. ----
    private static readonly Color UpColor = Color.FromRgb(0x16, 0xC7, 0x84);
    private static readonly Color DownColor = Color.FromRgb(0xEA, 0x39, 0x43);

    private static readonly Typeface AxisFace = new("Segoe UI");
    private static readonly Typeface MonoFace = new("Consolas");

    private static readonly Brush BackgroundBrush = Solid(Color.FromRgb(0x0F, 0x12, 0x17));
    private static readonly Brush AxisTextBrush = Solid(Color.FromRgb(0x8A, 0x93, 0xA3));
    private static readonly Brush ReadoutTextBrush = Solid(Color.FromRgb(0xE6, 0xEA, 0xF2));
    private static readonly Brush ReadoutFillBrush = Solid(Color.FromArgb(0xE0, 0x1B, 0x21, 0x2A));
    private static readonly Brush CrosshairTagBrush = Solid(Color.FromRgb(0x3A, 0x42, 0x50));
    private static readonly Brush WhiteBrush = Solid(Colors.White);
    private static readonly Brush UpBrush = Solid(UpColor);
    private static readonly Brush DownBrush = Solid(DownColor);

    // Soft fill under the line: opaque-ish at the line, fading to nothing at the bottom.
    private static readonly Brush UpAreaBrush = Fade(UpColor);
    private static readonly Brush DownAreaBrush = Fade(DownColor);

    private static readonly Pen GridPen = MakePen(Color.FromRgb(0x1E, 0x24, 0x2E), 1);
    private static readonly Pen CrosshairPen = MakePen(Color.FromRgb(0x6A, 0x73, 0x84), 1, dashed: true);
    private static readonly Pen UpLinePen = MakePen(UpColor, 2, round: true);
    private static readonly Pen DownLinePen = MakePen(DownColor, 2, round: true);
    private static readonly Pen UpGlowPen = MakePen(Color.FromArgb(0x30, UpColor.R, UpColor.G, UpColor.B), 8, round: true);
    private static readonly Pen DownGlowPen = MakePen(Color.FromArgb(0x30, DownColor.R, DownColor.G, DownColor.B), 8, round: true);
    private static readonly Pen UpLastLinePen = MakePen(Color.FromArgb(0x90, UpColor.R, UpColor.G, UpColor.B), 1, dashed: true);
    private static readonly Pen DownLastLinePen = MakePen(Color.FromArgb(0x90, DownColor.R, DownColor.G, DownColor.B), 1, dashed: true);
    private static readonly Pen UpWickPen = MakePen(UpColor, 1);
    private static readonly Pen DownWickPen = MakePen(DownColor, 1);

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // A vertical gradient from the color (translucent) at the top to fully transparent at the
    // bottom. The 90 is the gradient angle in degrees: 0 = left-to-right, 90 = top-to-bottom.
    private static LinearGradientBrush Fade(Color color)
    {
        var brush = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B),
                                            Color.FromArgb(0x00, color.R, color.G, color.B), 90);
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Color color, double thickness, bool round = false, bool dashed = false)
    {
        var pen = new Pen(Solid(color), thickness);
        if (round)
        {
            pen.LineJoin = PenLineJoin.Round;
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }

    // ---- State ----
    private Point? _mouse;                    // pointer position over the control; null when outside
    private double _viewMin = double.NaN;     // the price range currently DISPLAYED (eases toward the target)
    private double _viewMax = double.NaN;

    // What to draw. MainWindow sets these properties.
    private PriceHistory? _history;
    public PriceHistory? History
    {
        get => _history;
        set
        {
            _history = value;
            _viewMin = double.NaN;   // a different market has a different price range: snap, don't ease
            _viewMax = double.NaN;
        }
    }

    public double WindowSeconds { get; set; } = 120;       // how much time fits across the chart
    public ChartMode Mode { get; set; } = ChartMode.Line;
    public string PriceFormat { get; set; } = "F2";        // decimals for the readout and price tag

    // How long OnRender takes to RECORD its drawing commands (milliseconds, smoothed). This is
    // not the time to paint pixels (WPF does that later on its own render thread); it is the cost
    // our code adds to the UI thread each frame. Shown in the developer stats.
    public double LastRenderMilliseconds { get; private set; }

    public PriceChartControl()
    {
        ClipToBounds = true;
    }

    // ---- Mouse: only remember where the pointer is. The next frame draws the crosshair. ----
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
    }

    // ---- Drawing ----
    protected override void OnRender(DrawingContext dc)
    {
        long started = Stopwatch.GetTimestamp();
        Draw(dc);
        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        LastRenderMilliseconds = LastRenderMilliseconds * 0.9 + ms * 0.1;   // moving average, smooths out jitter
    }

    private void Draw(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Full-size background. WPF only sends mouse events to painted pixels, so this is also
        // what lets the crosshair work over "empty" areas.
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        // The plot area is where the data is drawn; axis labels go in the margins around it.
        // Too small to draw anything useful below 60x60.
        double plotLeft = LeftPad;
        double plotTop = TopPad;
        double plotWidth = width - LeftPad - RightAxisWidth;
        double plotHeight = height - TopPad - BottomAxisHeight;
        if (plotWidth < 60 || plotHeight < 60) return;
        double plotRight = plotLeft + plotWidth;
        double plotBottom = plotTop + plotHeight;

        var history = _history;
        DateTime now = DateTime.UtcNow;
        double windowSeconds = WindowSeconds;
        DateTime from = now - TimeSpan.FromSeconds(windowSeconds);

        // Candles are rebuilt from the history every frame (cheap: a few thousand points).
        TimeSpan candleInterval = TimeSpan.FromSeconds(windowSeconds / CandleCount);
        List<Candle>? candles = null;

        // ---- Work out the price range that must fit on screen ----
        double dataMin, dataMax;
        bool haveData;
        if (history == null)
        {
            haveData = false;
            dataMin = dataMax = 0;
        }
        else if (Mode == ChartMode.Candles)
        {
            candles = history.ToCandles(candleInterval, from);
            haveData = candles.Count > 0;
            dataMin = haveData ? candles.Min(c => c.Low) : 0;
            dataMax = haveData ? candles.Max(c => c.High) : 0;
        }
        else
        {
            haveData = history.TryGetRange(from, out dataMin, out dataMax);
        }

        if (!haveData || history == null || history.Latest == null)
        {
            DrawText(dc, "Collecting prices...", AxisFace, 15, AxisTextBrush, pixelsPerDip,
                     plotLeft + plotWidth / 2, plotTop + plotHeight / 2 - 18, TextAlignment.Center);
            DrawText(dc, "The chart fills in live as prices arrive. It starts empty each time you open the app.",
                     AxisFace, 12, AxisTextBrush, pixelsPerDip,
                     plotLeft + plotWidth / 2, plotTop + plotHeight / 2 + 4, TextAlignment.Center);
            return;
        }

        // ---- Vertical scale: add 12% padding above and below, then EASE toward it ----
        // Even a perfectly flat market needs a visible scale, so the span is never smaller than
        // 0.01% of the price (about $8 for BTC). The data sits in the middle of that span.
        double mid = (dataMin + dataMax) / 2;
        double span = Math.Max(dataMax - dataMin, Math.Abs(mid) * 1e-4);
        double targetMin = mid - span / 2 - span * 0.12;
        double targetMax = mid + span / 2 + span * 0.12;

        if (double.IsNaN(_viewMin))
        {
            _viewMin = targetMin;
            _viewMax = targetMax;
        }
        else
        {
            // Move 20% of the remaining distance each frame: fast at first, then settles smoothly.
            _viewMin += (targetMin - _viewMin) * 0.2;
            _viewMax += (targetMax - _viewMax) * 0.2;
        }
        double viewMin = _viewMin;
        double viewMax = _viewMax;

        // Local functions (methods declared inside a method) that can use the variables above.
        // X converts a time to a horizontal pixel, Y a price to a vertical pixel. Screen y grows
        // downward, so Y is flipped: a higher price gives a smaller y.
        double X(DateTime time) => plotLeft + (time - from).TotalSeconds / windowSeconds * plotWidth;
        double Y(double price) => plotTop + (viewMax - price) / (viewMax - viewMin) * plotHeight;

        PricePoint latest = history.Latest.Value;
        int firstVisible = history.FirstIndexAtOrAfter(from);
        double windowStartPrice = history.Points[Math.Min(firstVisible, history.Count - 1)].Price;
        bool isUp = latest.Price >= windowStartPrice;   // green if up over the window, red if down
        Brush trendBrush = isUp ? UpBrush : DownBrush;

        // ---- Grid lines and the price axis (right): roughly 5 lines at "nice" prices ----
        double step = NiceStep((viewMax - viewMin) / 5);
        int decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));   // enough decimals to show the step
        string axisFormat = "N" + decimals;   // "N" adds thousands separators: 84,760.5
        double firstGrid = Math.Ceiling(viewMin / step) * step;
        for (int i = 0; i < 30; i++)
        {
            double price = firstGrid + i * step;
            if (price > viewMax) break;
            double y = Y(price);
            dc.DrawLine(GridPen, new Point(plotLeft, y), new Point(plotRight, y));
            DrawText(dc, price.ToString(axisFormat, CultureInfo.InvariantCulture), AxisFace, 11, AxisTextBrush,
                     pixelsPerDip, plotRight + 8, y - 8, TextAlignment.Left);
        }

        // ---- Time axis (bottom): labels sit at fixed clock times (e.g. every :20s), so they
        //      glide left as time passes. The first label is the first multiple after 'from'. ----
        double labelEvery = windowSeconds <= 60 ? 10 : windowSeconds <= 120 ? 20 : 60;
        long labelTicks = TimeSpan.FromSeconds(labelEvery).Ticks;
        DateTime label = new DateTime((from.Ticks / labelTicks + 1) * labelTicks, DateTimeKind.Utc);
        for (; label <= now; label = label.AddSeconds(labelEvery))
        {
            double x = X(label);
            dc.DrawLine(GridPen, new Point(x, plotTop), new Point(x, plotBottom));
            DrawText(dc, label.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), AxisFace, 11,
                     AxisTextBrush, pixelsPerDip, x, plotBottom + 5, TextAlignment.Center);
        }

        // ---- The data itself. PushClip keeps it out of the axis margins until the matching Pop(). ----
        dc.PushClip(new RectangleGeometry(new Rect(plotLeft, plotTop, plotWidth, plotHeight)));
        if (Mode == ChartMode.Candles && candles != null)
        {
            DrawCandles(dc, candles, candleInterval, plotWidth, X, Y);
        }
        else
        {
            DrawLine(dc, history, firstVisible, now, plotBottom, isUp, X, Y);
        }
        dc.Pop();

        // ---- Latest price: dashed line across, a price tag on the axis, a pulsing dot ----
        double lastY = Y(latest.Price);
        dc.DrawLine(isUp ? UpLastLinePen : DownLastLinePen, new Point(plotLeft, lastY), new Point(plotRight, lastY));
        DrawTag(dc, latest.Price.ToString(PriceFormat, CultureInfo.InvariantCulture), trendBrush, WhiteBrush,
                pixelsPerDip, plotRight + 4, lastY);

        // The halo grows and fades over a repeating 1.6-second cycle; 'pulse' goes 0 -> 1.
        double pulse = (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency % 1.6) / 1.6;
        dc.PushOpacity(0.55 * (1 - pulse));
        dc.DrawEllipse(trendBrush, null, new Point(plotRight, lastY), 4 + 14 * pulse, 4 + 14 * pulse);   // the expanding halo
        dc.Pop();
        dc.DrawEllipse(trendBrush, null, new Point(plotRight, lastY), 5, 5);
        dc.DrawEllipse(WhiteBrush, null, new Point(plotRight, lastY), 2, 2);

        // ---- Crosshair and readout, only while the mouse is over the plot ----
        // ('_mouse is Point mouse' checks for a value and unwraps it into 'mouse' in one step.)
        if (_mouse is Point mouse && mouse.X >= plotLeft && mouse.X <= plotRight && mouse.Y >= plotTop && mouse.Y <= plotBottom)
        {
            dc.DrawLine(CrosshairPen, new Point(mouse.X, plotTop), new Point(mouse.X, plotBottom));
            dc.DrawLine(CrosshairPen, new Point(plotLeft, mouse.Y), new Point(plotRight, mouse.Y));

            // The price at the pointer's height, as a tag on the price axis.
            double priceAtMouse = viewMax - (mouse.Y - plotTop) / plotHeight * (viewMax - viewMin);
            DrawTag(dc, priceAtMouse.ToString(PriceFormat, CultureInfo.InvariantCulture), CrosshairTagBrush,
                    WhiteBrush, pixelsPerDip, plotRight + 4, mouse.Y);

            // The time at the pointer's position, as a filled tag on the time axis (filled so it
            // covers any axis label underneath).
            DateTime timeAtMouse = from + TimeSpan.FromSeconds((mouse.X - plotLeft) / plotWidth * windowSeconds);
            var timeTag = MakeText(timeAtMouse.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                                   AxisFace, 11, WhiteBrush, pixelsPerDip);
            var timeRect = new Rect(mouse.X - timeTag.Width / 2 - 6, plotBottom + 2, timeTag.Width + 12, 20);
            dc.DrawRoundedRectangle(CrosshairTagBrush, null, timeRect, 3, 3);
            dc.DrawText(timeTag, new Point(timeRect.Left + 6, timeRect.Top + (timeRect.Height - timeTag.Height) / 2));

            string readout = BuildReadout(history, candles, candleInterval, timeAtMouse);
            if (readout.Length > 0)
            {
                DrawReadout(dc, readout, pixelsPerDip, plotLeft + 8, plotTop + 8);
            }
        }
    }

    // ---- Series drawing ----

    private void DrawLine(DrawingContext dc, PriceHistory history, int firstVisible, DateTime now,
                          double plotBottom, bool isUp, Func<DateTime, double> X, Func<double, double> Y)
    {
        var points = history.Points;
        int first = Math.Max(0, firstVisible - 1);   // start one point early so the line reaches the left edge
        PricePoint last = points[points.Count - 1];
        double xNow = X(now);

        // StreamGeometry is WPF's lightweight shape type: a list of segments with no per-segment
        // objects. One holds the line, the other the filled area under it (the line's points plus
        // the two bottom corners).
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext lineCtx = line.Open())
        using (StreamGeometryContext areaCtx = area.Open())
        {
            double x0 = X(points[first].Time);
            double y0 = Y(points[first].Price);
            lineCtx.BeginFigure(new Point(x0, y0), false, false);
            areaCtx.BeginFigure(new Point(x0, plotBottom), true, true);
            areaCtx.LineTo(new Point(x0, y0), false, false);

            for (int i = first + 1; i < points.Count; i++)
            {
                var p = new Point(X(points[i].Time), Y(points[i].Price));
                lineCtx.LineTo(p, true, true);
                areaCtx.LineTo(p, false, false);
            }

            // Extend the last price flat out to "now" so the line glides smoothly between samples.
            var end = new Point(xNow, Y(last.Price));
            lineCtx.LineTo(end, true, true);
            areaCtx.LineTo(end, false, false);
            areaCtx.LineTo(new Point(xNow, plotBottom), false, false);
        }
        line.Freeze();
        area.Freeze();

        dc.DrawGeometry(isUp ? UpAreaBrush : DownAreaBrush, null, area);
        dc.DrawGeometry(null, isUp ? UpGlowPen : DownGlowPen, line);   // a wide, faint copy underneath makes the glow
        dc.DrawGeometry(null, isUp ? UpLinePen : DownLinePen, line);
    }

    private void DrawCandles(DrawingContext dc, List<Candle> candles, TimeSpan interval, double plotWidth,
                             Func<DateTime, double> X, Func<double, double> Y)
    {
        double slotWidth = plotWidth / CandleCount;            // horizontal space one candle owns
        double bodyWidth = Math.Max(1, slotWidth * 0.7);       // leave a gap between candles

        foreach (var candle in candles)
        {
            bool up = candle.Close >= candle.Open;
            double xCenter = X(candle.Start + TimeSpan.FromTicks(interval.Ticks / 2));

            // The wick: a thin line from the lowest to the highest price in the slice.
            dc.DrawLine(up ? UpWickPen : DownWickPen, new Point(xCenter, Y(candle.High)), new Point(xCenter, Y(candle.Low)));

            // The body: a box from open to close. At least 1px tall so a flat candle is still visible.
            double top = Y(Math.Max(candle.Open, candle.Close));
            double bottom = Y(Math.Min(candle.Open, candle.Close));
            dc.DrawRectangle(up ? UpBrush : DownBrush, null,
                             new Rect(xCenter - bodyWidth / 2, top, bodyWidth, Math.Max(1, bottom - top)));
        }
    }

    // ---- Crosshair readout text ----

    // The readout for the time under the pointer: open/high/low/close in candle mode, or the
    // nearest recorded price in line mode. Empty if there is nothing there.
    private string BuildReadout(PriceHistory? history, List<Candle>? candles, TimeSpan interval, DateTime timeAtMouse)
    {
        if (history == null || history.Count == 0) return "";
        string time = timeAtMouse.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        if (Mode == ChartMode.Candles && candles != null)
        {
            // Which candle's time slice contains the pointer?
            foreach (var c in candles)
            {
                if (timeAtMouse >= c.Start && timeAtMouse < c.Start + interval)
                {
                    return $"{time}   O {Fmt(c.Open)}   H {Fmt(c.High)}   L {Fmt(c.Low)}   C {Fmt(c.Close)}";
                }
            }
            return "";
        }

        // Line mode: find the recorded point closest in time to the pointer.
        var points = history.Points;
        int index = history.FirstIndexAtOrAfter(timeAtMouse);
        if (index >= points.Count) index = points.Count - 1;
        if (index > 0 && (timeAtMouse - points[index - 1].Time) < (points[index].Time - timeAtMouse)) index--;
        return $"{time}   Price {Fmt(points[index].Price)}";
    }

    private string Fmt(double price) => price.ToString(PriceFormat, CultureInfo.InvariantCulture);

    // ---- Small drawing helpers ----

    // Picks a "nice" grid spacing near 'raw': 1, 2, or 5 times a power of ten, so the axis reads
    // 84,740 / 84,760 / 84,780 rather than 84,743.3 / 84,766.6.
    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;
        double exponent = Math.Floor(Math.Log10(raw));
        double magnitude = Math.Pow(10, exponent);
        double fraction = raw / magnitude;
        double nice = fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10;
        return nice * magnitude;
    }

    // A rounded label with text, vertically centered on y, used for the price tags on the axis.
    private void DrawTag(DrawingContext dc, string text, Brush fill, Brush textBrush, double pixelsPerDip, double left, double centerY)
    {
        var formatted = MakeText(text, AxisFace, 11, textBrush, pixelsPerDip);
        var rect = new Rect(left, centerY - 10, RightAxisWidth - 6, 20);
        dc.DrawRoundedRectangle(fill, null, rect, 3, 3);
        dc.DrawText(formatted, new Point(rect.Left + (rect.Width - formatted.Width) / 2, rect.Top + (rect.Height - formatted.Height) / 2));
    }

    private void DrawReadout(DrawingContext dc, string text, double pixelsPerDip, double left, double top)
    {
        var formatted = MakeText(text, MonoFace, 12, ReadoutTextBrush, pixelsPerDip);
        var rect = new Rect(left, top, formatted.Width + 16, formatted.Height + 10);
        dc.DrawRoundedRectangle(ReadoutFillBrush, null, rect, 4, 4);
        dc.DrawText(formatted, new Point(rect.Left + 8, rect.Top + 5));
    }

    // Draws text at a horizontal anchor. 'anchorX' is the left edge for Left, the right edge for
    // Right, and the middle for Center.
    private static void DrawText(DrawingContext dc, string text, Typeface face, double size, Brush brush,
                                 double pixelsPerDip, double anchorX, double top, TextAlignment align)
    {
        var formatted = MakeText(text, face, size, brush, pixelsPerDip);
        double x = align switch
        {
            TextAlignment.Right => anchorX - formatted.Width,
            TextAlignment.Center => anchorX - formatted.Width / 2,
            _ => anchorX,
        };
        dc.DrawText(formatted, new Point(x, top));
    }

    private static FormattedText MakeText(string text, Typeface face, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);
}
