namespace TradingApp;

// One remembered price: "at this moment, the price was this".
public readonly record struct PricePoint(DateTime Time, double Price);

// One candlestick: a summary of all the prices seen during one slice of time.
//   Open  = the first price in the slice      High = the highest
//   Close = the last price in the slice       Low  = the lowest
// If Close >= Open the price rose during the slice (drawn green); otherwise it fell (red).
public readonly record struct Candle(DateTime Start, double Open, double High, double Low, double Close);

// A rolling window of recent prices for ONE market, recorded live while the app runs.
//
// Design notes:
//  * It only keeps the last 'retention' worth of points, so memory stays bounded however long
//    the app runs.
//  * The CALLER supplies each timestamp (nothing here reads the clock), so tests can feed it
//    exact times and check exact results.
//  * NOT thread-safe. It is owned by the UI thread: the frame timer adds samples and the chart
//    reads them, both on that thread, so no locking is needed. (Order books, by contrast, cross
//    threads and therefore use immutable snapshots.)
public sealed class PriceHistory
{
    private readonly List<PricePoint> _points = new();
    private readonly TimeSpan _retention;

    public PriceHistory(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        _retention = retention;
    }

    public int Count => _points.Count;

    // Oldest first. Exposed read-only so the chart can loop over it without copying.
    public IReadOnlyList<PricePoint> Points => _points;

    // The newest point, or null if nothing has been recorded. '[^1]' means "last element".
    public PricePoint? Latest => _points.Count > 0 ? _points[^1] : null;

    // Records a price. Returns false (and records nothing) if the price is NaN or infinite, or
    // the timestamp is older than the newest point already stored. Equal timestamps are allowed.
    public bool Add(DateTime time, double price)
    {
        if (double.IsNaN(price) || double.IsInfinity(price)) return false;
        if (_points.Count > 0 && time < _points[^1].Time) return false;

        _points.Add(new PricePoint(time, price));
        DropExpired(time);
        return true;
    }

    // Forget points older than (now - retention). A point exactly at the cutoff is kept.
    private void DropExpired(DateTime now)
    {
        DateTime cutoff = now - _retention;
        int expired = 0;
        while (expired < _points.Count && _points[expired].Time < cutoff) expired++;

        // Removing from the front of a List shifts the rest down. At ~3,000 small points that is
        // cheap; a ring buffer would avoid it if it ever mattered.
        if (expired > 0) _points.RemoveRange(0, expired);
    }

    // Index of the first point whose time is >= cutoff (Count if there is none). Binary search:
    // the points are sorted by time, so we can halve the search range each step.
    public int FirstIndexAtOrAfter(DateTime cutoff)
    {
        int low = 0, high = _points.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_points[mid].Time < cutoff) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    // Lowest and highest price among points at or after 'from'. Returns false if there are none.
    // The results come back through the 'out' parameters.
    public bool TryGetRange(DateTime from, out double min, out double max)
    {
        min = double.MaxValue;
        max = double.MinValue;

        int start = FirstIndexAtOrAfter(from);
        if (start >= _points.Count) return false;

        for (int i = start; i < _points.Count; i++)
        {
            double price = _points[i].Price;
            if (price < min) min = price;
            if (price > max) max = price;
        }
        return true;
    }

    // Groups the points at or after 'from' into fixed-size time slices and summarizes each as a
    // candle. Slices are aligned to the clock (e.g. 5-second slices start at :00, :05, :10...),
    // so a candle keeps the same boundaries as time passes. Empty slices produce no candle.
    public List<Candle> ToCandles(TimeSpan interval, DateTime from)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

        var candles = new List<Candle>();
        long intervalTicks = interval.Ticks;

        for (int i = FirstIndexAtOrAfter(from); i < _points.Count; i++)
        {
            PricePoint point = _points[i];

            // Integer division rounds down to the start of this point's slice.
            DateTime sliceStart = new DateTime(point.Time.Ticks / intervalTicks * intervalTicks, point.Time.Kind);

            if (candles.Count > 0 && candles[^1].Start == sliceStart)
            {
                // Same slice as the previous point: widen High/Low and move Close.
                // 'with' makes a copy of a record with some fields changed.
                Candle current = candles[^1];
                candles[^1] = current with
                {
                    High = Math.Max(current.High, point.Price),
                    Low = Math.Min(current.Low, point.Price),
                    Close = point.Price,
                };
            }
            else
            {
                candles.Add(new Candle(sliceStart, point.Price, point.Price, point.Price, point.Price));
            }
        }

        return candles;
    }
}
