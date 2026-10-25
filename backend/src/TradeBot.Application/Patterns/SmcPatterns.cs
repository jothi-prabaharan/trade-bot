using System.Text.Json.Nodes;
using TradeBot.Application.Validation;
using TradeBot.Domain.Enums;
using static TradeBot.Domain.Enums.PatternDirection;

namespace TradeBot.Application.Patterns;

/// <summary>
/// Smart-money / price-action setups on fractal swings. These trigger on the bar that breaks or rejects
/// the level, so they are confirmed on detection; target is <c>targetRr</c> × risk unless a pivot level is used.
/// </summary>
internal static class SmcCatalog
{
    private static JsonObject Defaults(bool volume) => new()
    {
        ["swingStrength"] = 2m, ["targetRr"] = 2.0m, ["slAtrBuffer"] = 0.15m, ["lookbackBars"] = 40m, ["equalTolAtr"] = 0.4m,
        ["slAtr"] = 1.0m, ["touches"] = 2m, ["confirmWithinBars"] = 1m, ["quality"] = volume ? 0.8m : 0.85m,
    };

    private static IPatternDetector D(string code, string name, PatternDirection dir, decimal weight, bool volume, Func<PatternContext, string, PatternDirection, IEnumerable<PatternSetup>> f) =>
        new FuncDetector(new PatternDefinition(code, name, PatternCategory.SmcStructure, dir, 30, weight, volume, true, Defaults(volume)), c => f(c, code, dir));

    public static IReadOnlyList<IPatternDetector> All { get; } =
    [
        D("SMC_BOS_BULL", "Bullish Break of Structure", Bullish, 66, true, (c, code, dir) => StructureBreak(c, code, dir, changeOfCharacter: false)),
        D("SMC_BOS_BEAR", "Bearish Break of Structure", Bearish, 66, true, (c, code, dir) => StructureBreak(c, code, dir, changeOfCharacter: false)),
        D("SMC_CHOCH_BULL", "Bullish Change of Character", Bullish, 64, true, (c, code, dir) => StructureBreak(c, code, dir, changeOfCharacter: true)),
        D("SMC_CHOCH_BEAR", "Bearish Change of Character", Bearish, 64, true, (c, code, dir) => StructureBreak(c, code, dir, changeOfCharacter: true)),
        D("SMC_ORDER_BLOCK_BULL", "Bullish Order Block Retest", Bullish, 66, false, OrderBlock),
        D("SMC_ORDER_BLOCK_BEAR", "Bearish Order Block Retest", Bearish, 66, false, OrderBlock),
        D("SMC_FVG_BULL", "Bullish Fair Value Gap Retest", Bullish, 62, false, FairValueGap),
        D("SMC_FVG_BEAR", "Bearish Fair Value Gap Retest", Bearish, 62, false, FairValueGap),
        D("SMC_SR_BREAKOUT_BULL", "Resistance Breakout", Bullish, 64, true, SupportResistance),
        D("SMC_SR_BREAKDOWN_BEAR", "Support Breakdown", Bearish, 64, true, SupportResistance),
        D("SMC_CPR_BREAKOUT_BULL", "CPR Breakout", Bullish, 62, true, Cpr),
        D("SMC_CPR_BREAKDOWN_BEAR", "CPR Breakdown", Bearish, 62, true, Cpr),
    ];

    private static List<Pivot> Swings(PatternContext c)
    {
        var key = $"fr:{c.P["swingStrength"]}";
        if (!c.Cache.TryGetValue(key, out var cached)) c.Cache[key] = cached = PivotFinder.Fractals(c.Bars, c.P.Int("swingStrength"));
        return (List<Pivot>)cached;
    }

    private static IEnumerable<PatternSetup> Immediate(PatternContext c, string code, PatternDirection dir, decimal sl, IReadOnlyList<Pivot> pivots, string key)
    {
        var s = SetupMath.RiskMultiple(code, dir, c.Bars[c.Last].Close, sl, c.P["targetRr"], c.P["quality"], pivots, key, confirmedOnDetection: true);
        return s is null ? [] : [s];
    }

    private static IEnumerable<PatternSetup> StructureBreak(PatternContext c, string code, PatternDirection dir, bool changeOfCharacter)
    {
        var i = c.Last;
        var swings = Swings(c);
        var highs = swings.Where(p => p.IsHigh && p.Index < i).TakeLast(2).ToArray();
        var lows = swings.Where(p => !p.IsHigh && p.Index < i).TakeLast(2).ToArray();
        if (highs.Length < 2 || lows.Length < 2) return [];

        var bullStructure = highs[1].Price > highs[0].Price && lows[1].Price > lows[0].Price;
        var bearStructure = highs[1].Price < highs[0].Price && lows[1].Price < lows[0].Price;
        var (close, prev) = (c.Bars[i].Close, c.Bars[i - 1].Close);
        var buffer = c.P["slAtrBuffer"] * c.AtrAt(i);

        if (dir == Bullish && close > highs[1].Price && prev <= highs[1].Price && (changeOfCharacter ? bearStructure : bullStructure))
            return Immediate(c, code, dir, lows[1].Price - buffer, [.. highs, .. lows], $"{code}@{highs[1].Time.Ticks}");
        if (dir == Bearish && close < lows[1].Price && prev >= lows[1].Price && (changeOfCharacter ? bullStructure : bearStructure))
            return Immediate(c, code, dir, highs[1].Price + buffer, [.. highs, .. lows], $"{code}@{lows[1].Time.Ticks}");
        return [];
    }

    private static IEnumerable<PatternSetup> OrderBlock(PatternContext c, string code, PatternDirection dir)
    {
        var b = c.Bars;
        var k = c.Kit;
        var i = c.Last;
        var bull = dir == Bullish;
        var swings = Swings(c);
        var from = Math.Max(1, i - c.P.Int("lookbackBars"));

        // Most recent displacement bar that broke a prior swing in the trade direction
        for (var brk = i - 1; brk >= from; brk--)
        {
            var swing = swings.LastOrDefault(p => p.IsHigh == bull && p.Index < brk - c.P.Int("swingStrength"));
            if (swing is null) continue;
            var broke = bull ? b[brk].Close > swing.Price && b[brk - 1].Close <= swing.Price : b[brk].Close < swing.Price && b[brk - 1].Close >= swing.Price;
            if (!broke) continue;

            var ob = Enumerable.Range(Math.Max(0, brk - 10), brk - Math.Max(0, brk - 10)).Reverse().FirstOrDefault(j => bull ? k.Bear(j) : k.Bull(j), -1);
            if (ob < 0) return [];
            var (zoneLo, zoneHi) = (b[ob].Low, b[ob].High);

            // First retest only, and the block must still be intact
            for (var j = brk + 1; j < i; j++)
                if (bull ? b[j].Low <= zoneHi || b[j].Close < zoneLo : b[j].High >= zoneLo || b[j].Close > zoneHi) return [];

            var rejects = bull
                ? b[i].Low <= zoneHi && b[i].Close > zoneHi && k.Bull(i)
                : b[i].High >= zoneLo && b[i].Close < zoneLo && k.Bear(i);
            if (!rejects) return [];
            var buffer = c.P["slAtrBuffer"] * c.AtrAt(i);
            Pivot[] pivots = [new(ob, b[ob].Time, bull ? zoneLo : zoneHi, !bull), swing];
            return Immediate(c, code, dir, bull ? zoneLo - buffer : zoneHi + buffer, pivots, $"{code}@{b[ob].Time.Ticks}");
        }
        return [];
    }

    private static IEnumerable<PatternSetup> FairValueGap(PatternContext c, string code, PatternDirection dir)
    {
        var b = c.Bars;
        var k = c.Kit;
        var i = c.Last;
        var bull = dir == Bullish;
        for (var j = i - 3; j >= Math.Max(0, i - c.P.Int("lookbackBars")); j--)
        {
            var gap = bull ? b[j + 2].Low > b[j].High : b[j + 2].High < b[j].Low;
            if (!gap || !(bull ? k.Bull(j + 1) : k.Bear(j + 1)) || !k.LongBody(j + 1)) continue;
            var (gapLo, gapHi) = bull ? (b[j].High, b[j + 2].Low) : (b[j + 2].High, b[j].Low);

            var untouched = true;
            for (var m = j + 3; m < i && untouched; m++) untouched = bull ? b[m].Low > gapHi : b[m].High < gapLo;
            if (!untouched) return [];

            var rejects = bull
                ? b[i].Low <= gapHi && b[i].Low >= gapLo && b[i].Close > gapHi
                : b[i].High >= gapLo && b[i].High <= gapHi && b[i].Close < gapLo;
            if (!rejects) return [];
            var buffer = c.P["slAtrBuffer"] * c.AtrAt(i);
            Pivot[] pivots = [new(j, b[j].Time, bull ? gapLo : gapHi, !bull), new(j + 2, b[j + 2].Time, bull ? gapHi : gapLo, bull)];
            return Immediate(c, code, dir, bull ? gapLo - buffer : gapHi + buffer, pivots, $"{code}@{b[j].Time.Ticks}");
        }
        return [];
    }

    private static IEnumerable<PatternSetup> SupportResistance(PatternContext c, string code, PatternDirection dir)
    {
        var i = c.Last;
        var bull = dir == Bullish;
        var atr = c.AtrAt(i);
        var tol = c.P["equalTolAtr"] * atr;
        var strength = c.P.Int("swingStrength");
        var swings = Swings(c).Where(p => p.IsHigh == bull && p.Index < i - strength && p.Index >= i - 3 * c.P.Int("lookbackBars")).ToList();
        var (close, prev) = (c.Bars[i].Close, c.Bars[i - 1].Close);

        // Nearest cluster of equal swing points that this bar has just closed through
        var cluster = swings
            .Select(s => swings.Where(o => Math.Abs(o.Price - s.Price) <= tol).ToList())
            .Where(g => g.Count >= c.P.Int("touches"))
            .Select(g => (Level: bull ? g.Max(p => p.Price) : g.Min(p => p.Price), Points: g))
            .Where(g => bull ? close > g.Level && prev <= g.Level : close < g.Level && prev >= g.Level)
            .OrderBy(g => Math.Abs(close - g.Level))
            .FirstOrDefault();
        if (cluster.Points is null) return [];

        var sl = bull ? cluster.Level - c.P["slAtr"] * atr : cluster.Level + c.P["slAtr"] * atr;
        return Immediate(c, code, dir, sl, cluster.Points, $"{code}@{cluster.Level}:{cluster.Points[^1].Time.Ticks}");
    }

    private static IEnumerable<PatternSetup> Cpr(PatternContext c, string code, PatternDirection dir)
    {
        var b = c.Bars;
        var i = c.Last;
        var today = DateOnly.FromDateTime(FixedLimits.ToIst(b[i].Time));
        var prevDay = b.Take(i).Select(x => DateOnly.FromDateTime(FixedLimits.ToIst(x.Time))).Where(d => d < today).DefaultIfEmpty().Max();
        if (prevDay == default) return [];
        var prior = b.Where(x => DateOnly.FromDateTime(FixedLimits.ToIst(x.Time)) == prevDay).ToList();
        var (h, l, cl) = (prior.Max(x => x.High), prior.Min(x => x.Low), prior[^1].Close);

        var pivot = (h + l + cl) / 3;
        var bc = (h + l) / 2;
        var tc = 2 * pivot - bc;
        if (tc < bc) (tc, bc) = (bc, tc);
        var (close, prev) = (b[i].Close, b[i - 1].Close);
        var buffer = c.P["slAtrBuffer"] * c.AtrAt(i);
        var key = $"{code}@{today}";

        if (dir == Bullish && close > tc && prev <= tc)
        {
            var target = new[] { 2 * pivot - l, pivot + (h - l) }.FirstOrDefault(t => t > close);
            if (target == 0) return [];
            var s = SetupMath.Measured(code, dir, close, bc - buffer, target, c.P["quality"], [], key, confirmedOnDetection: true);
            return s is null ? [] : [s];
        }
        if (dir == Bearish && close < bc && prev >= bc)
        {
            var target = new[] { 2 * pivot - h, pivot - (h - l) }.FirstOrDefault(t => t < close && t > 0);
            if (target == 0) return [];
            var s = SetupMath.Measured(code, dir, close, tc + buffer, target, c.P["quality"], [], key, confirmedOnDetection: true);
            return s is null ? [] : [s];
        }
        return [];
    }
}
