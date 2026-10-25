using TradeBot.Application.Market;

namespace TradeBot.Application.Patterns;

public readonly record struct Line(decimal Slope, decimal Intercept)
{
    public decimal At(int index) => Intercept + Slope * index;

    public static Line Through(Pivot a, Pivot b) =>
        a.Index == b.Index ? new Line(0, a.Price) : FromPointSlope(a.Index, a.Price, (b.Price - a.Price) / (b.Index - a.Index));

    public static Line FromPointSlope(int x, decimal y, decimal slope) => new(slope, y - slope * x);

    /// <summary>Least-squares fit over (index, value) points.</summary>
    public static Line Fit(IReadOnlyList<(int X, decimal Y)> pts)
    {
        if (pts.Count == 1) return new Line(0, pts[0].Y);
        decimal n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in pts) { sx += x; sy += y; sxx += (decimal)x * x; sxy += x * y; }
        var den = n * sxx - sx * sx;
        var slope = den == 0 ? 0 : (n * sxy - sx * sy) / den;
        return new Line(slope, (sy - slope * sx) / n);
    }
}

public static class PivotFinder
{
    /// <summary>
    /// ATR-threshold zigzag over bars[0..end]: a swing is confirmed once price reverses by
    /// <c>atrMult × ATR</c> from its extreme. Pivots alternate high/low.
    /// </summary>
    public static List<Pivot> ZigZag(IReadOnlyList<Bar> bars, decimal[] atr, decimal atrMult)
    {
        var pivots = new List<Pivot>();
        if (bars.Count < 3) return pivots;
        int dir = 0, hiIdx = 0, loIdx = 0;
        decimal hi = bars[0].High, lo = bars[0].Low;

        for (var i = 1; i < bars.Count; i++)
        {
            var b = bars[i];
            var thr = Math.Max(atr[i] * atrMult, b.Close * 0.001m);
            switch (dir)
            {
                case 0:
                    if (b.High > hi) { hi = b.High; hiIdx = i; }
                    if (b.Low < lo) { lo = b.Low; loIdx = i; }
                    if (hiIdx < i && hi - b.Low >= thr) { Add(true, hiIdx, hi); dir = -1; lo = b.Low; loIdx = i; }
                    else if (loIdx < i && b.High - lo >= thr) { Add(false, loIdx, lo); dir = 1; hi = b.High; hiIdx = i; }
                    break;
                case 1:
                    if (b.High > hi) { hi = b.High; hiIdx = i; }
                    else if (hi - b.Low >= thr) { Add(true, hiIdx, hi); dir = -1; lo = b.Low; loIdx = i; }
                    break;
                default:
                    if (b.Low < lo) { lo = b.Low; loIdx = i; }
                    else if (b.High - lo >= thr) { Add(false, loIdx, lo); dir = 1; hi = b.High; hiIdx = i; }
                    break;
            }
        }
        return pivots;

        void Add(bool isHigh, int idx, decimal price) => pivots.Add(new Pivot(idx, bars[idx].Time, price, isHigh));
    }

    /// <summary>Fractal swings: a bar whose high (low) beats <paramref name="strength"/> bars on both sides.</summary>
    public static List<Pivot> Fractals(IReadOnlyList<Bar> bars, int strength)
    {
        var pivots = new List<Pivot>();
        for (var j = strength; j < bars.Count - strength; j++)
        {
            bool isHigh = true, isLow = true;
            for (var d = 1; d <= strength && (isHigh || isLow); d++)
            {
                if (bars[j - d].High >= bars[j].High || bars[j + d].High > bars[j].High) isHigh = false;
                if (bars[j - d].Low <= bars[j].Low || bars[j + d].Low < bars[j].Low) isLow = false;
            }
            if (isHigh) pivots.Add(new Pivot(j, bars[j].Time, bars[j].High, true));
            if (isLow) pivots.Add(new Pivot(j, bars[j].Time, bars[j].Low, false));
        }
        return pivots;
    }

    /// <summary>The last pivots when their high/low sequence matches <paramref name="shape"/> (e.g. "HLH").</summary>
    public static Pivot[]? Tail(IReadOnlyList<Pivot> pivots, string shape)
    {
        if (pivots.Count < shape.Length) return null;
        var tail = pivots.Skip(pivots.Count - shape.Length).ToArray();
        for (var k = 0; k < shape.Length; k++)
            if (tail[k].IsHigh != (shape[k] == 'H')) return null;
        return tail;
    }
}
