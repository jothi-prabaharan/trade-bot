using TradeBot.Application.Validation;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Market;

/// <summary>OHLCV bar; <see cref="Time"/> is the bucket start in UTC.</summary>
public sealed record Bar(DateTime Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume, long? OpenInterest = null)
{
    public decimal Body => Math.Abs(Close - Open);
    public decimal Range => High - Low;
    public decimal UpperShadow => High - Math.Max(Open, Close);
    public decimal LowerShadow => Math.Min(Open, Close) - Low;
    public bool IsBull => Close > Open;
    public bool IsBear => Close < Open;
    public decimal Mid => (High + Low) / 2;
}

/// <summary>Series key: the instrument's continuous futures (SecurityId null) or a specific contract.</summary>
public readonly record struct SeriesKey(long InstrumentId, long? SecurityId);

/// <summary>A tick from the feed. CumulativeVolume is the day volume; AverageTradedPrice is the exchange's session VWAP.</summary>
public readonly record struct Tick(long SecurityId, decimal Ltp, DateTime TimeUtc, long? CumulativeVolume = null, long? OpenInterest = null, decimal? AverageTradedPrice = null);

public sealed record ClosedBar(SeriesKey Series, Timeframe Timeframe, Bar Bar);

public static class Timeframes
{
    public static readonly Timeframe[] Intraday = [Timeframe.M1, Timeframe.M3, Timeframe.M5, Timeframe.M15, Timeframe.H1];

    public static TimeSpan Span(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M3 => TimeSpan.FromMinutes(3),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.D1 => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(tf)),
    };

    public static int Rank(Timeframe tf) => Array.IndexOf(Enum.GetValues<Timeframe>(), tf);

    /// <summary>Bucket start (UTC) anchored to the 09:15 IST session open, or null outside the session.</summary>
    public static DateTime? BucketStart(DateTime utc, Timeframe tf)
    {
        var ist = FixedLimits.ToIst(utc);
        var open = ist.Date + FixedLimits.MarketOpen.ToTimeSpan();
        var close = ist.Date + FixedLimits.MarketCloseExclusive.ToTimeSpan();
        if (ist < open || ist >= close) return null;
        if (tf == Timeframe.D1) return DateTime.SpecifyKind(open - FixedLimits.IstOffset, DateTimeKind.Utc);
        var span = Span(tf);
        var start = open + TimeSpan.FromTicks((ist - open).Ticks / span.Ticks * span.Ticks);
        return DateTime.SpecifyKind(start - FixedLimits.IstOffset, DateTimeKind.Utc);
    }
}

/// <summary>Builds multi-timeframe bars from ticks; emits a bar once its bucket is over. Not thread-safe; one per feed loop.</summary>
public sealed class CandleAggregator(IReadOnlyCollection<Timeframe> timeframes)
{
    private sealed class Working
    {
        public DateTime Start;
        public decimal O, H, L, C;
        public long V;
        public long? Oi;
    }

    private readonly Dictionary<(SeriesKey, Timeframe), Working> _open = new();
    private readonly Dictionary<SeriesKey, long> _lastCumVolume = new();

    public IEnumerable<ClosedBar> OnTick(SeriesKey series, Tick tick)
    {
        var closed = new List<ClosedBar>();
        long volume = 0;
        if (tick.CumulativeVolume is { } cum)
        {
            if (_lastCumVolume.TryGetValue(series, out var prev) && cum >= prev) volume = cum - prev;
            _lastCumVolume[series] = cum;
        }

        foreach (var tf in timeframes)
        {
            if (Timeframes.BucketStart(tick.TimeUtc, tf) is not { } start) continue;
            var key = (series, tf);
            if (_open.TryGetValue(key, out var w) && w.Start != start)
            {
                if (start > w.Start) closed.Add(Close(series, tf, w));
                else continue; // late tick for an already-closed bucket
                w = null;
            }
            if (w is null)
            {
                _open[key] = new Working { Start = start, O = tick.Ltp, H = tick.Ltp, L = tick.Ltp, C = tick.Ltp, V = volume, Oi = tick.OpenInterest };
                continue;
            }
            w.H = Math.Max(w.H, tick.Ltp);
            w.L = Math.Min(w.L, tick.Ltp);
            w.C = tick.Ltp;
            w.V += volume;
            w.Oi = tick.OpenInterest ?? w.Oi;
        }
        return closed;
    }

    /// <summary>Closes buckets whose end has passed (illiquid contracts may not tick on time).</summary>
    public IEnumerable<ClosedBar> Flush(DateTime nowUtc)
    {
        var closed = new List<ClosedBar>();
        foreach (var ((series, tf), w) in _open.ToList())
        {
            var end = tf == Timeframe.D1
                ? w.Start + (FixedLimits.MarketCloseExclusive - FixedLimits.MarketOpen)
                : w.Start + Timeframes.Span(tf);
            if (nowUtc < end) continue;
            closed.Add(Close(series, tf, w));
            _open.Remove((series, tf));
        }
        return closed;
    }

    public Bar? Forming(SeriesKey series, Timeframe tf) =>
        _open.TryGetValue((series, tf), out var w) ? new Bar(w.Start, w.O, w.H, w.L, w.C, w.V, w.Oi) : null;

    private static ClosedBar Close(SeriesKey s, Timeframe tf, Working w) => new(s, tf, new Bar(w.Start, w.O, w.H, w.L, w.C, w.V, w.Oi));
}

/// <summary>Bounded per-series bar history shared by the feed thread (writes) and the scanner (reads).</summary>
public sealed class BarHistory(int capacity = 600)
{
    private readonly Dictionary<(SeriesKey, Timeframe), List<Bar>> _bars = new();
    private readonly Lock _lock = new();

    public void Add(SeriesKey series, Timeframe tf, Bar bar)
    {
        lock (_lock)
        {
            if (!_bars.TryGetValue((series, tf), out var list)) _bars[(series, tf)] = list = [];
            if (list.Count > 0 && list[^1].Time >= bar.Time)
            {
                if (list[^1].Time == bar.Time) list[^1] = bar;
                return;
            }
            list.Add(bar);
            if (list.Count > capacity) list.RemoveRange(0, list.Count - capacity);
        }
    }

    public void Load(SeriesKey series, Timeframe tf, IEnumerable<Bar> bars)
    {
        foreach (var b in bars.OrderBy(b => b.Time)) Add(series, tf, b);
    }

    /// <summary>Snapshot copy; safe to use while new bars arrive.</summary>
    public IReadOnlyList<Bar> Get(SeriesKey series, Timeframe tf)
    {
        lock (_lock) return _bars.TryGetValue((series, tf), out var list) ? list.ToList() : [];
    }

    public int Count(SeriesKey series, Timeframe tf)
    {
        lock (_lock) return _bars.TryGetValue((series, tf), out var list) ? list.Count : 0;
    }
}
