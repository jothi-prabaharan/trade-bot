using System.Text.Json.Nodes;
using TradeBot.Domain.Enums;
using static TradeBot.Domain.Enums.PatternDirection;

namespace TradeBot.Application.Patterns;

internal sealed class FuncDetector(PatternDefinition definition, Func<PatternContext, IEnumerable<PatternSetup>> detect) : IPatternDetector
{
    public PatternDefinition Definition { get; } = definition;
    public IEnumerable<PatternSetup> Detect(PatternContext ctx) => ctx.Bars.Count < Definition.MinCandles ? [] : detect(ctx);
}

/// <summary>Zigzag-pivot geometric patterns. Breakouts must close beyond the neckline/trendline.</summary>
internal static class ClassicalCatalog
{
    private static JsonObject Defaults() => new()
    {
        ["zigzagAtr"] = 1.5m, ["equalTolAtr"] = 0.5m, ["minHeightAtr"] = 2.0m, ["slAtrBuffer"] = 0.2m, ["targetMult"] = 1.0m,
        ["confirmWithinBars"] = 20m, ["maxBarsSinceLastPivot"] = 12m, ["minSpacingBars"] = 3m, ["quality"] = 0.85m,
    };

    private static JsonObject With(params (string Key, decimal Value)[] extra)
    {
        var d = Defaults();
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static IPatternDetector D(string code, string name, PatternDirection dir, int minCandles, decimal weight, Func<PatternContext, IEnumerable<PatternSetup>> f, JsonObject? p = null) =>
        new FuncDetector(new PatternDefinition(code, name, PatternCategory.Classical, dir, minCandles, weight, true, true, p ?? Defaults()), f);

    public static IReadOnlyList<IPatternDetector> All { get; } =
    [
        D("CLS_DOUBLE_TOP", "Double Top", Bearish, 40, 70, c => DoubleTopBottom(c, "CLS_DOUBLE_TOP", top: true)),
        D("CLS_DOUBLE_BOTTOM", "Double Bottom", Bullish, 40, 70, c => DoubleTopBottom(c, "CLS_DOUBLE_BOTTOM", top: false)),
        D("CLS_TRIPLE_TOP", "Triple Top", Bearish, 60, 72, c => TripleTopBottom(c, "CLS_TRIPLE_TOP", top: true)),
        D("CLS_TRIPLE_BOTTOM", "Triple Bottom", Bullish, 60, 72, c => TripleTopBottom(c, "CLS_TRIPLE_BOTTOM", top: false)),
        D("CLS_HEAD_SHOULDERS", "Head and Shoulders", Bearish, 60, 75, c => HeadShoulders(c, "CLS_HEAD_SHOULDERS", inverse: false), With(("shoulderTolAtr", 1.5m))),
        D("CLS_INVERSE_HEAD_SHOULDERS", "Inverse Head and Shoulders", Bullish, 60, 75, c => HeadShoulders(c, "CLS_INVERSE_HEAD_SHOULDERS", inverse: true), With(("shoulderTolAtr", 1.5m))),
        D("CLS_ASCENDING_TRIANGLE", "Ascending Triangle", Bullish, 40, 70, c => Triangle(c, "CLS_ASCENDING_TRIANGLE", Bullish)),
        D("CLS_DESCENDING_TRIANGLE", "Descending Triangle", Bearish, 40, 70, c => Triangle(c, "CLS_DESCENDING_TRIANGLE", Bearish)),
        D("CLS_SYMMETRICAL_TRIANGLE", "Symmetrical Triangle", Bidirectional, 40, 66, c => Triangle(c, "CLS_SYMMETRICAL_TRIANGLE", Bidirectional)),
        D("CLS_RISING_WEDGE", "Rising Wedge", Bearish, 40, 68, c => Wedge(c, "CLS_RISING_WEDGE", rising: true)),
        D("CLS_FALLING_WEDGE", "Falling Wedge", Bullish, 40, 68, c => Wedge(c, "CLS_FALLING_WEDGE", rising: false)),
        D("CLS_RECTANGLE", "Rectangle", Bidirectional, 40, 64, c => Rectangle(c)),
        D("CLS_PRICE_CHANNEL", "Price Channel", Bidirectional, 40, 62, c => Channel(c), With(("slopeTol", 0.35m))),
        D("CLS_BROADENING", "Broadening Formation", Bidirectional, 40, 58, c => Broadening(c)),
        D("CLS_BULL_FLAG", "Bull Flag", Bullish, 30, 72, c => FlagOrPennant(c, "CLS_BULL_FLAG", Bullish, pennant: false), FlagDefaults()),
        D("CLS_BEAR_FLAG", "Bear Flag", Bearish, 30, 72, c => FlagOrPennant(c, "CLS_BEAR_FLAG", Bearish, pennant: false), FlagDefaults()),
        D("CLS_BULL_PENNANT", "Bull Pennant", Bullish, 30, 70, c => FlagOrPennant(c, "CLS_BULL_PENNANT", Bullish, pennant: true), FlagDefaults()),
        D("CLS_BEAR_PENNANT", "Bear Pennant", Bearish, 30, 70, c => FlagOrPennant(c, "CLS_BEAR_PENNANT", Bearish, pennant: true), FlagDefaults()),
        D("CLS_CUP_HANDLE", "Cup and Handle", Bullish, 50, 72, c => CupHandle(c, "CLS_CUP_HANDLE", inverse: false), With(("cupMinBars", 10m))),
        D("CLS_INVERSE_CUP_HANDLE", "Inverse Cup and Handle", Bearish, 50, 70, c => CupHandle(c, "CLS_INVERSE_CUP_HANDLE", inverse: true), With(("cupMinBars", 10m))),
    ];

    private static JsonObject FlagDefaults() => With(("poleMinAtr", 3.0m), ("poleMaxBars", 12m), ("flagMinBars", 3m), ("flagMaxBars", 15m), ("maxRetrace", 0.5m), ("confirmWithinBars", 6m));

    // ---- shared helpers ----
    private sealed record Geo(int I, decimal Atr, decimal Tol, decimal MinHeight, decimal Buffer, decimal Close, IReadOnlyList<Pivot> Pivots);

    private static Geo? Prepare(PatternContext c, string shape, out Pivot[] tail)
    {
        tail = [];
        var key = $"zz:{c.P["zigzagAtr"]}";
        if (!c.Cache.TryGetValue(key, out var cached)) c.Cache[key] = cached = PivotFinder.ZigZag(c.Bars, c.Atr, c.P["zigzagAtr"]);
        var pivots = (List<Pivot>)cached;
        if (PivotFinder.Tail(pivots, shape) is not { } t) return null;
        var i = c.Last;
        if (i - t[^1].Index > c.P.Int("maxBarsSinceLastPivot")) return null;
        tail = t;
        var atr = c.AtrAt(i);
        return new Geo(i, atr, c.P["equalTolAtr"] * atr, c.P["minHeightAtr"] * atr, c.P["slAtrBuffer"] * atr, c.Bars[i].Close, pivots);
    }

    private static string Key(string code, IEnumerable<Pivot> pivots) => $"{code}@{string.Join(',', pivots.Select(p => p.Time.Ticks))}";

    private static IEnumerable<PatternSetup> One(PatternSetup? s) => s is null ? [] : [s];

    // ---- detectors ----
    private static IEnumerable<PatternSetup> DoubleTopBottom(PatternContext c, string code, bool top)
    {
        if (Prepare(c, top ? "HLH" : "LHL", out var t) is not { } g) return [];
        var (p1, mid, p2) = (t[0], t[1], t[2]);
        if (Math.Abs(p1.Price - p2.Price) > g.Tol || p2.Index - p1.Index < c.P.Int("minSpacingBars")) return [];
        var extreme = top ? Math.Max(p1.Price, p2.Price) : Math.Min(p1.Price, p2.Price);
        var height = Math.Abs(extreme - mid.Price);
        if (height < g.MinHeight) return [];
        if (top ? g.Close <= mid.Price || g.Close >= extreme : g.Close >= mid.Price || g.Close <= extreme) return [];

        var q = c.P["quality"] * (1 - 0.5m * Math.Abs(p1.Price - p2.Price) / Math.Max(g.Tol, 1e-9m));
        var dir = top ? Bearish : Bullish;
        var sl = top ? extreme + g.Buffer : extreme - g.Buffer;
        var target = top ? mid.Price - c.P["targetMult"] * height : mid.Price + c.P["targetMult"] * height;
        return One(SetupMath.Measured(code, dir, mid.Price, sl, target, q, t, Key(code, t)));
    }

    private static IEnumerable<PatternSetup> TripleTopBottom(PatternContext c, string code, bool top)
    {
        if (Prepare(c, top ? "HLHLH" : "LHLHL", out var t) is not { } g) return [];
        var peaks = new[] { t[0].Price, t[2].Price, t[4].Price };
        if (peaks.Max() - peaks.Min() > g.Tol) return [];
        var neck = top ? Math.Min(t[1].Price, t[3].Price) : Math.Max(t[1].Price, t[3].Price);
        var extreme = top ? peaks.Max() : peaks.Min();
        var height = Math.Abs(extreme - neck);
        if (height < g.MinHeight) return [];
        if (top ? g.Close <= neck || g.Close >= extreme : g.Close >= neck || g.Close <= extreme) return [];

        var q = c.P["quality"] * (1 - 0.4m * (peaks.Max() - peaks.Min()) / Math.Max(g.Tol, 1e-9m));
        var dir = top ? Bearish : Bullish;
        var sl = top ? extreme + g.Buffer : extreme - g.Buffer;
        var target = top ? neck - c.P["targetMult"] * height : neck + c.P["targetMult"] * height;
        return One(SetupMath.Measured(code, dir, neck, sl, target, q, t, Key(code, t)));
    }

    private static IEnumerable<PatternSetup> HeadShoulders(PatternContext c, string code, bool inverse)
    {
        if (Prepare(c, inverse ? "LHLHL" : "HLHLH", out var t) is not { } g) return [];
        var (ls, n1, hd, n2, rs) = (t[0], t[1], t[2], t[3], t[4]);
        var sign = inverse ? -1 : 1;
        if (sign * (hd.Price - ls.Price) <= g.Tol || sign * (hd.Price - rs.Price) <= g.Tol) return [];
        if (Math.Abs(ls.Price - rs.Price) > c.P["shoulderTolAtr"] * g.Atr) return [];

        var neck = Line.Through(n1, n2);
        if (sign * (ls.Price - neck.At(ls.Index)) <= 0 || sign * (rs.Price - neck.At(rs.Index)) <= 0) return [];
        var height = sign * (hd.Price - neck.At(hd.Index));
        if (height < g.MinHeight || Math.Abs(neck.Slope * (rs.Index - ls.Index)) > height) return [];

        var neckNow = neck.At(g.I);
        if (sign * (g.Close - neckNow) <= 0 || sign * (g.Close - rs.Price) >= 0) return [];
        var dir = inverse ? Bullish : Bearish;
        var sl = rs.Price + sign * g.Buffer;
        var target = neckNow - sign * c.P["targetMult"] * height;
        var symmetry = 1 - Math.Min(1, Math.Abs(ls.Price - rs.Price) / (c.P["shoulderTolAtr"] * g.Atr));
        return One(SetupMath.Measured(code, dir, new LevelLine(neckNow, neck.Slope), sl, target, c.P["quality"] * (0.7m + 0.3m * symmetry), t, Key(code, t)));
    }

    private static (Pivot[] Highs, Pivot[] Lows)? HighsLows(PatternContext c, out Geo? g, out Pivot[] tail)
    {
        g = Prepare(c, "HLHL", out tail) ?? Prepare(c, "LHLH", out tail);
        if (g is null) return null;
        return (tail.Where(p => p.IsHigh).ToArray(), tail.Where(p => !p.IsHigh).ToArray());
    }

    private static IEnumerable<PatternSetup> Triangle(PatternContext c, string code, PatternDirection kind)
    {
        if (HighsLows(c, out var g, out var t) is not { } hl || g is null) return [];
        var (hs, ls) = hl;
        var step = g.Tol * 0.25m;
        var upper = Line.Through(hs[0], hs[1]);
        var lower = Line.Through(ls[0], ls[1]);
        var start = Math.Min(hs[0].Index, ls[0].Index);
        var height = upper.At(start) - lower.At(start);
        if (height < g.MinHeight) return [];
        var (up, lo) = (upper.At(g.I), lower.At(g.I));
        if (g.Close >= up || g.Close <= lo || up - lo <= 0) return [];
        var key = Key(code, t);
        var q = c.P["quality"];
        var bullSl = ls[1].Price - g.Buffer;
        var bearSl = hs[1].Price + g.Buffer;

        switch (kind)
        {
            case Bullish when Math.Abs(hs[0].Price - hs[1].Price) <= g.Tol && ls[1].Price > ls[0].Price + step:
                var flatTop = Math.Max(hs[0].Price, hs[1].Price);
                return One(SetupMath.Measured(code, Bullish, flatTop, bullSl, flatTop + c.P["targetMult"] * height, q, t, key));
            case Bearish when Math.Abs(ls[0].Price - ls[1].Price) <= g.Tol && hs[1].Price < hs[0].Price - step:
                var flatBottom = Math.Min(ls[0].Price, ls[1].Price);
                return One(SetupMath.Measured(code, Bearish, flatBottom, bearSl, flatBottom - c.P["targetMult"] * height, q, t, key));
            case Bidirectional when hs[1].Price < hs[0].Price - step && ls[1].Price > ls[0].Price + step:
                return new[]
                {
                    SetupMath.Measured(code, Bullish, new LevelLine(up, upper.Slope), bullSl, up + c.P["targetMult"] * height, q, t, key + ":B"),
                    SetupMath.Measured(code, Bearish, new LevelLine(lo, lower.Slope), bearSl, lo - c.P["targetMult"] * height, q, t, key + ":S"),
                }.OfType<PatternSetup>();
            default:
                return [];
        }
    }

    private static IEnumerable<PatternSetup> Wedge(PatternContext c, string code, bool rising)
    {
        if (HighsLows(c, out var g, out var t) is not { } hl || g is null) return [];
        var (hs, ls) = hl;
        var upper = Line.Through(hs[0], hs[1]);
        var lower = Line.Through(ls[0], ls[1]);
        var converging = rising
            ? upper.Slope > 0 && lower.Slope > upper.Slope
            : lower.Slope < 0 && upper.Slope < lower.Slope;
        if (!converging) return [];
        var start = Math.Min(hs[0].Index, ls[0].Index);
        var width = upper.At(start) - lower.At(start);
        var (up, lo) = (upper.At(g.I), lower.At(g.I));
        if (width < g.MinHeight || up - lo <= 0 || g.Close >= up || g.Close <= lo) return [];
        var key = Key(code, t);
        return rising
            ? One(SetupMath.Measured(code, Bearish, new LevelLine(lo, lower.Slope), hs[1].Price + g.Buffer, lo - c.P["targetMult"] * width, c.P["quality"], t, key))
            : One(SetupMath.Measured(code, Bullish, new LevelLine(up, upper.Slope), ls[1].Price - g.Buffer, up + c.P["targetMult"] * width, c.P["quality"], t, key));
    }

    private static IEnumerable<PatternSetup> Rectangle(PatternContext c)
    {
        const string code = "CLS_RECTANGLE";
        if (HighsLows(c, out var g, out var t) is not { } hl || g is null) return [];
        var (hs, ls) = hl;
        if (Math.Abs(hs[0].Price - hs[1].Price) > g.Tol || Math.Abs(ls[0].Price - ls[1].Price) > g.Tol) return [];
        var top = Math.Max(hs[0].Price, hs[1].Price);
        var bottom = Math.Min(ls[0].Price, ls[1].Price);
        var height = top - bottom;
        if (height < g.MinHeight || g.Close >= top || g.Close <= bottom) return [];
        var mid = (top + bottom) / 2;
        var key = Key(code, t);
        return new[]
        {
            SetupMath.Measured(code, Bullish, top, mid, top + c.P["targetMult"] * height, c.P["quality"], t, key + ":B"),
            SetupMath.Measured(code, Bearish, bottom, mid, bottom - c.P["targetMult"] * height, c.P["quality"], t, key + ":S"),
        }.OfType<PatternSetup>();
    }

    private static IEnumerable<PatternSetup> Channel(PatternContext c)
    {
        const string code = "CLS_PRICE_CHANNEL";
        if (HighsLows(c, out var g, out var t) is not { } hl || g is null) return [];
        var (hs, ls) = hl;
        var upper = Line.Through(hs[0], hs[1]);
        var lower = Line.Through(ls[0], ls[1]);
        var maxSlope = Math.Max(Math.Abs(upper.Slope), Math.Abs(lower.Slope));
        if (maxSlope < 0.05m * g.Atr || Math.Sign(upper.Slope) != Math.Sign(lower.Slope)
            || Math.Abs(upper.Slope - lower.Slope) > c.P["slopeTol"] * maxSlope) return [];
        var (up, lo) = (upper.At(g.I), lower.At(g.I));
        var height = up - lo;
        if (height < g.MinHeight || g.Close >= up || g.Close <= lo) return [];
        var mid = (up + lo) / 2;
        var key = Key(code, t);
        return new[]
        {
            SetupMath.Measured(code, Bullish, new LevelLine(up, upper.Slope), mid, up + c.P["targetMult"] * height, c.P["quality"], t, key + ":B"),
            SetupMath.Measured(code, Bearish, new LevelLine(lo, lower.Slope), mid, lo - c.P["targetMult"] * height, c.P["quality"], t, key + ":S"),
        }.OfType<PatternSetup>();
    }

    private static IEnumerable<PatternSetup> Broadening(PatternContext c)
    {
        const string code = "CLS_BROADENING";
        if (HighsLows(c, out var g, out var t) is not { } hl || g is null) return [];
        var (hs, ls) = hl;
        var step = g.Tol * 0.25m;
        if (hs[1].Price <= hs[0].Price + step || ls[1].Price >= ls[0].Price - step) return [];
        var (top, bottom) = (hs[1].Price, ls[1].Price);
        var height = top - bottom;
        if (height < g.MinHeight || g.Close >= top || g.Close <= bottom) return [];
        var mid = (top + bottom) / 2;
        var key = Key(code, t);
        return new[]
        {
            SetupMath.Measured(code, Bullish, top, mid, top + c.P["targetMult"] * height, c.P["quality"] * 0.9m, t, key + ":B"),
            SetupMath.Measured(code, Bearish, bottom, mid, bottom - c.P["targetMult"] * height, c.P["quality"] * 0.9m, t, key + ":S"),
        }.OfType<PatternSetup>();
    }

    private static IEnumerable<PatternSetup> FlagOrPennant(PatternContext c, string code, PatternDirection dir, bool pennant)
    {
        var b = c.Bars;
        var i = c.Last;
        var bull = dir == Bullish;
        var poleMax = c.P.Int("poleMaxBars");
        for (var f = c.P.Int("flagMinBars"); f <= c.P.Int("flagMaxBars"); f++)
        {
            var top = i - f;
            if (top - poleMax < 0) yield break;

            var poleEnd = bull ? b[top].High : b[top].Low;
            var flagBars = Enumerable.Range(top + 1, f).ToArray();
            if (bull ? flagBars.Any(j => b[j].High > poleEnd) : flagBars.Any(j => b[j].Low < poleEnd)) continue;

            var baseIdx = Enumerable.Range(top - poleMax, poleMax).MinBy(j => bull ? b[j].Low : -b[j].High);
            var poleStart = bull ? b[baseIdx].Low : b[baseIdx].High;
            var pole = Math.Abs(poleEnd - poleStart);
            if (top - baseIdx < 2 || pole < c.P["poleMinAtr"] * c.AtrAt(top)) continue;

            var flagExtreme = bull ? flagBars.Min(j => b[j].Low) : flagBars.Max(j => b[j].High);
            if (Math.Abs(poleEnd - flagExtreme) > c.P["maxRetrace"] * pole) continue;

            var highs = Line.Fit(flagBars.Select(j => (j, b[j].High)).ToList());
            var lows = Line.Fit(flagBars.Select(j => (j, b[j].Low)).ToList());
            var shapeOk = pennant
                ? highs.Slope < 0 && lows.Slope > 0
                : bull ? highs.Slope <= 0 && lows.Slope <= 0 && Math.Abs(highs.Slope - lows.Slope) <= 0.5m * Math.Max(Math.Abs(highs.Slope), Math.Abs(lows.Slope)) + 1e-9m
                       : highs.Slope >= 0 && lows.Slope >= 0 && Math.Abs(highs.Slope - lows.Slope) <= 0.5m * Math.Max(Math.Abs(highs.Slope), Math.Abs(lows.Slope)) + 1e-9m;
            if (!shapeOk) continue;

            var line = bull ? highs : lows;
            var level = line.At(i);
            if (bull ? b[i].Close >= level : b[i].Close <= level) continue;

            var buffer = c.P["slAtrBuffer"] * c.AtrAt(i);
            var sl = bull ? flagExtreme - buffer : flagExtreme + buffer;
            var target = bull ? level + c.P["targetMult"] * pole : level - c.P["targetMult"] * pole;
            Pivot[] pivots = [new(baseIdx, b[baseIdx].Time, poleStart, !bull), new(top, b[top].Time, poleEnd, bull)];
            var setup = SetupMath.Measured(code, dir, new LevelLine(level, line.Slope), sl, target, c.P["quality"], pivots, $"{code}@{b[top].Time.Ticks}");
            if (setup is not null) yield return setup;
            yield break;
        }
    }

    private static IEnumerable<PatternSetup> CupHandle(PatternContext c, string code, bool inverse)
    {
        if (Prepare(c, inverse ? "LHL" : "HLH", out var t) is not { } g) return [];
        var (left, cup, right) = (t[0], t[1], t[2]);
        var sign = inverse ? -1 : 1;
        var width = right.Index - left.Index;
        if (width < c.P.Int("cupMinBars") || Math.Abs(left.Price - right.Price) > 2 * g.Tol) return [];
        var pos = (decimal)(cup.Index - left.Index) / width;
        if (pos < 0.25m || pos > 0.75m) return [];
        var rim = inverse ? Math.Min(left.Price, right.Price) : Math.Max(left.Price, right.Price);
        var depth = inverse ? cup.Price - Math.Max(left.Price, right.Price) : Math.Min(left.Price, right.Price) - cup.Price;
        if (depth < 1.5m * g.MinHeight) return [];

        var handleBars = g.I - right.Index;
        if (handleBars < 2 || handleBars > width / 2) return [];
        var handleExtreme = inverse
            ? Enumerable.Range(right.Index + 1, handleBars).Max(j => c.Bars[j].High)
            : Enumerable.Range(right.Index + 1, handleBars).Min(j => c.Bars[j].Low);
        if (sign * (right.Price - handleExtreme) > 0.5m * depth) return [];
        if (sign * (g.Close - rim) >= 0) return [];

        var sl = handleExtreme - sign * g.Buffer;
        var target = rim + sign * c.P["targetMult"] * depth;
        return One(SetupMath.Measured(code, inverse ? Bearish : Bullish, rim, sl, target, c.P["quality"], t, Key(code, t)));
    }
}
