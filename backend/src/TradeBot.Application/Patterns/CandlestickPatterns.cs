using System.Text.Json.Nodes;
using TradeBot.Domain.Enums;
using static TradeBot.Domain.Enums.PatternDirection;

namespace TradeBot.Application.Patterns;

internal delegate bool CandleMatch(CandleKit k, int i);

/// <summary>Custom breakout/SL for patterns whose trigger isn't the pattern's high/low (e.g. Hikkake).</summary>
internal delegate (decimal Breakout, decimal Sl) CandleLevels(CandleKit k, int i, PatternDirection dir);

internal sealed record CandleSpec(string Code, string Name, PatternDirection Direction, int Candles, decimal Weight, CandleMatch Match, CandleLevels? Levels = null);

/// <summary>
/// Candlestick detector: on match, arms a breakout beyond the pattern's extreme (next close must cross it),
/// SL at the opposite extreme minus an ATR buffer, target at <c>targetRr</c> × risk. Neutral candles arm both sides.
/// </summary>
internal sealed class CandlestickDetector(CandleSpec spec) : IPatternDetector
{
    public PatternDefinition Definition { get; } = new(
        spec.Code, spec.Name, PatternCategory.Candlestick, spec.Direction, spec.Candles, spec.Weight,
        RequireVolumeConfirm: false, RequireBreakoutClose: true,
        new JsonObject { ["targetRr"] = 2.0m, ["slAtrBuffer"] = 0.1m, ["confirmWithinBars"] = 3m, ["quality"] = 0.8m });

    public IEnumerable<PatternSetup> Detect(PatternContext ctx)
    {
        var i = ctx.Last;
        if (i < spec.Candles + 9 || !spec.Match(ctx.Kit, i)) yield break;

        var first = i - spec.Candles + 1;
        var buffer = ctx.P["slAtrBuffer"] * ctx.AtrAt(i);
        var key = $"{spec.Code}@{ctx.Bars[i].Time:O}";
        var dirs = spec.Direction is Neutral or Bidirectional ? new[] { Bullish, Bearish } : [spec.Direction];

        foreach (var dir in dirs)
        {
            var (breakout, extreme) = spec.Levels?.Invoke(ctx.Kit, i, dir) ?? (
                dir == Bullish ? Max(ctx, first, i, true) : Max(ctx, first, i, false),
                dir == Bullish ? Max(ctx, first, i, false) : Max(ctx, first, i, true));
            var sl = dir == Bullish ? extreme - buffer : extreme + buffer;
            var setup = SetupMath.RiskMultiple(spec.Code, dir, breakout, sl, ctx.P["targetRr"], ctx.P["quality"], [], $"{key}:{dir}");
            if (setup is not null) yield return setup;
        }
    }

    private static decimal Max(PatternContext ctx, int from, int to, bool high)
    {
        var v = high ? decimal.MinValue : decimal.MaxValue;
        for (var j = from; j <= to; j++) v = high ? Math.Max(v, ctx.Bars[j].High) : Math.Min(v, ctx.Bars[j].Low);
        return v;
    }
}

/// <summary>The 61 TA-Lib candlestick patterns, split into directional codes where the pattern has a bull and bear form.</summary>
internal static class CandlestickCatalog
{
    private static bool SoldierBar(CandleKit k, int j) => k.Bull(j) && !k.SmallBody(j) && k.Up(j) <= k.Body(j) * 0.25m;
    private static bool CrowBar(CandleKit k, int j) => k.Bear(j) && !k.SmallBody(j) && k.Lo(j) <= k.Body(j) * 0.25m;
    private static bool EngulfBull(CandleKit k, int i) => k.Bear(i - 1) && k.Bull(i) && k.O(i) <= k.C(i - 1) && k.C(i) >= k.O(i - 1) && (k.O(i) < k.C(i - 1) || k.C(i) > k.O(i - 1));
    private static bool EngulfBear(CandleKit k, int i) => k.Bull(i - 1) && k.Bear(i) && k.O(i) >= k.C(i - 1) && k.C(i) <= k.O(i - 1) && (k.O(i) > k.C(i - 1) || k.C(i) < k.O(i - 1));
    private static bool HaramiBull(CandleKit k, int i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.SmallBody(i) && k.Top(i) < k.O(i - 1) && k.Bot(i) > k.C(i - 1);
    private static bool HaramiBear(CandleKit k, int i) => k.Bull(i - 1) && k.LongBody(i - 1) && k.SmallBody(i) && k.Top(i) < k.C(i - 1) && k.Bot(i) > k.O(i - 1);
    private static bool HammerShape(CandleKit k, int i) => k.SmallBody(i) && k.Lo(i) >= 2 * k.Body(i) && k.Lo(i) >= 0.6m * k.Range(i) && k.UpVeryShort(i);
    private static bool InvHammerShape(CandleKit k, int i) => k.SmallBody(i) && k.Up(i) >= 2 * k.Body(i) && k.Up(i) >= 0.6m * k.Range(i) && k.LoVeryShort(i);
    private static bool LongLegged(CandleKit k, int i) => k.Doji(i) && k.Up(i) >= 0.3m * k.Range(i) && k.Lo(i) >= 0.3m * k.Range(i);
    private static bool KickBull(CandleKit k, int i) => k.Bear(i - 1) && k.Marubozu(i - 1) && k.Bull(i) && k.Marubozu(i) && k.L(i) > k.H(i - 1);
    private static bool KickBear(CandleKit k, int i) => k.Bull(i - 1) && k.Marubozu(i - 1) && k.Bear(i) && k.Marubozu(i) && k.H(i) < k.L(i - 1);
    private static bool StarBase(CandleKit k, int i, bool bull) => bull
        ? k.Bear(i - 2) && k.LongBody(i - 2) && k.Top(i - 1) < k.Bot(i - 2) && k.Bull(i) && k.C(i) > k.C(i - 2) + 0.3m * k.Body(i - 2)
        : k.Bull(i - 2) && k.LongBody(i - 2) && k.Bot(i - 1) > k.Top(i - 2) && k.Bear(i) && k.C(i) < k.C(i - 2) - 0.3m * k.Body(i - 2);
    private static bool RisingCloses(CandleKit k, int from, int to) { for (var j = from + 1; j <= to; j++) if (k.C(j) <= k.C(j - 1)) return false; return true; }
    private static bool FallingCloses(CandleKit k, int from, int to) { for (var j = from + 1; j <= to; j++) if (k.C(j) >= k.C(j - 1)) return false; return true; }
    private static bool OpensInPrevBody(CandleKit k, int from, int to) { for (var j = from + 1; j <= to; j++) if (k.O(j) < k.Bot(j - 1) || k.O(j) > k.Top(j - 1)) return false; return true; }

    private static readonly CandleLevels HikkakeLevels = (k, i, dir) => dir == Bullish ? (k.H(i - 1), k.L(i)) : (k.L(i - 1), k.H(i));

    public static IReadOnlyList<CandleSpec> All { get; } =
    [
        // ---- single candle ----
        new("CDL_DOJI", "Doji", Neutral, 1, 45, (k, i) => k.Doji(i)),
        new("CDL_DRAGONFLY_DOJI", "Dragonfly Doji", Bullish, 1, 55, (k, i) => k.Doji(i) && k.UpVeryShort(i) && k.Lo(i) >= 0.6m * k.Range(i)),
        new("CDL_GRAVESTONE_DOJI", "Gravestone Doji", Bearish, 1, 55, (k, i) => k.Doji(i) && k.LoVeryShort(i) && k.Up(i) >= 0.6m * k.Range(i)),
        new("CDL_LONG_LEGGED_DOJI", "Long-Legged Doji", Neutral, 1, 45, LongLegged),
        new("CDL_RICKSHAW_MAN", "Rickshaw Man", Neutral, 1, 45, (k, i) => LongLegged(k, i) && Math.Abs((k.Top(i) + k.Bot(i)) / 2 - (k.H(i) + k.L(i)) / 2) <= 0.1m * k.Range(i)),
        new("CDL_TAKURI", "Takuri", Bullish, 1, 60, (k, i) => k.Doji(i) && k.UpVeryShort(i) && k.Lo(i) >= 0.75m * k.Range(i) && k.DownTrend(i)),
        new("CDL_HAMMER", "Hammer", Bullish, 1, 62, (k, i) => HammerShape(k, i) && k.DownTrend(i)),
        new("CDL_HANGING_MAN", "Hanging Man", Bearish, 1, 58, (k, i) => HammerShape(k, i) && k.UpTrend(i)),
        new("CDL_INVERTED_HAMMER", "Inverted Hammer", Bullish, 1, 55, (k, i) => InvHammerShape(k, i) && k.DownTrend(i)),
        new("CDL_SHOOTING_STAR", "Shooting Star", Bearish, 1, 62, (k, i) => InvHammerShape(k, i) && k.UpTrend(i)),
        new("CDL_MARUBOZU_BULL", "Bullish Marubozu", Bullish, 1, 60, (k, i) => k.Bull(i) && k.Marubozu(i)),
        new("CDL_MARUBOZU_BEAR", "Bearish Marubozu", Bearish, 1, 60, (k, i) => k.Bear(i) && k.Marubozu(i)),
        new("CDL_CLOSING_MARUBOZU_BULL", "Bullish Closing Marubozu", Bullish, 1, 58, (k, i) => k.Bull(i) && k.LongBody(i) && k.UpVeryShort(i)),
        new("CDL_CLOSING_MARUBOZU_BEAR", "Bearish Closing Marubozu", Bearish, 1, 58, (k, i) => k.Bear(i) && k.LongBody(i) && k.LoVeryShort(i)),
        new("CDL_BELT_HOLD_BULL", "Bullish Belt Hold", Bullish, 1, 58, (k, i) => k.Bull(i) && k.LongBody(i) && k.LoVeryShort(i) && k.DownTrend(i)),
        new("CDL_BELT_HOLD_BEAR", "Bearish Belt Hold", Bearish, 1, 58, (k, i) => k.Bear(i) && k.LongBody(i) && k.UpVeryShort(i) && k.UpTrend(i)),
        new("CDL_SPINNING_TOP", "Spinning Top", Neutral, 1, 40, (k, i) => k.SmallBody(i) && !k.Doji(i) && k.Up(i) > k.Body(i) && k.Lo(i) > k.Body(i)),
        new("CDL_HIGH_WAVE", "High Wave", Neutral, 1, 45, (k, i) => k.SmallBody(i) && !k.Doji(i) && k.Up(i) > 2 * k.Body(i) && k.Lo(i) > 2 * k.Body(i)),
        new("CDL_LONG_LINE_BULL", "Bullish Long Line", Bullish, 1, 50, (k, i) => k.Bull(i) && k.LongBody(i) && k.Up(i) < 0.25m * k.Body(i) && k.Lo(i) < 0.25m * k.Body(i)),
        new("CDL_LONG_LINE_BEAR", "Bearish Long Line", Bearish, 1, 50, (k, i) => k.Bear(i) && k.LongBody(i) && k.Up(i) < 0.25m * k.Body(i) && k.Lo(i) < 0.25m * k.Body(i)),
        new("CDL_SHORT_LINE_BULL", "Bullish Short Line", Bullish, 1, 40, (k, i) => k.Bull(i) && k.SmallBody(i) && !k.Doji(i) && k.Up(i) < k.Body(i) && k.Lo(i) < k.Body(i)),
        new("CDL_SHORT_LINE_BEAR", "Bearish Short Line", Bearish, 1, 40, (k, i) => k.Bear(i) && k.SmallBody(i) && !k.Doji(i) && k.Up(i) < k.Body(i) && k.Lo(i) < k.Body(i)),

        // ---- two candles ----
        new("CDL_ENGULFING_BULL", "Bullish Engulfing", Bullish, 2, 70, EngulfBull),
        new("CDL_ENGULFING_BEAR", "Bearish Engulfing", Bearish, 2, 70, EngulfBear),
        new("CDL_HARAMI_BULL", "Bullish Harami", Bullish, 2, 58, (k, i) => HaramiBull(k, i) && !k.Doji(i)),
        new("CDL_HARAMI_BEAR", "Bearish Harami", Bearish, 2, 58, (k, i) => HaramiBear(k, i) && !k.Doji(i)),
        new("CDL_HARAMI_CROSS_BULL", "Bullish Harami Cross", Bullish, 2, 60, (k, i) => HaramiBull(k, i) && k.Doji(i)),
        new("CDL_HARAMI_CROSS_BEAR", "Bearish Harami Cross", Bearish, 2, 60, (k, i) => HaramiBear(k, i) && k.Doji(i)),
        new("CDL_PIERCING", "Piercing Line", Bullish, 2, 66, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Bull(i) && k.LongBody(i) && k.O(i) < k.L(i - 1) && k.C(i) > k.C(i - 1) + 0.5m * k.Body(i - 1) && k.C(i) < k.O(i - 1)),
        new("CDL_DARK_CLOUD_COVER", "Dark Cloud Cover", Bearish, 2, 66, (k, i) => k.Bull(i - 1) && k.LongBody(i - 1) && k.Bear(i) && k.LongBody(i) && k.O(i) > k.H(i - 1) && k.C(i) < k.C(i - 1) - 0.5m * k.Body(i - 1) && k.C(i) > k.O(i - 1)),
        new("CDL_DOJI_STAR_BULL", "Bullish Doji Star", Bullish, 2, 58, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Doji(i) && k.BodyGapDown(i)),
        new("CDL_DOJI_STAR_BEAR", "Bearish Doji Star", Bearish, 2, 58, (k, i) => k.Bull(i - 1) && k.LongBody(i - 1) && k.Doji(i) && k.BodyGapUp(i)),
        new("CDL_COUNTERATTACK_BULL", "Bullish Counterattack", Bullish, 2, 60, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Bull(i) && k.LongBody(i) && Math.Abs(k.C(i) - k.C(i - 1)) <= k.Equal(i)),
        new("CDL_COUNTERATTACK_BEAR", "Bearish Counterattack", Bearish, 2, 60, (k, i) => k.Bull(i - 1) && k.LongBody(i - 1) && k.Bear(i) && k.LongBody(i) && Math.Abs(k.C(i) - k.C(i - 1)) <= k.Equal(i)),
        new("CDL_KICKING_BULL", "Bullish Kicking", Bullish, 2, 75, KickBull),
        new("CDL_KICKING_BEAR", "Bearish Kicking", Bearish, 2, 75, KickBear),
        new("CDL_KICKING_BY_LENGTH_BULL", "Bullish Kicking by Length", Bullish, 2, 72, (k, i) => (KickBull(k, i) && k.Body(i) >= k.Body(i - 1)) || (KickBear(k, i) && k.Body(i - 1) > k.Body(i))),
        new("CDL_KICKING_BY_LENGTH_BEAR", "Bearish Kicking by Length", Bearish, 2, 72, (k, i) => (KickBear(k, i) && k.Body(i) >= k.Body(i - 1)) || (KickBull(k, i) && k.Body(i - 1) > k.Body(i))),
        new("CDL_HOMING_PIGEON", "Homing Pigeon", Bullish, 2, 55, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Bear(i) && k.SmallBody(i) && k.O(i) < k.O(i - 1) && k.C(i) > k.C(i - 1)),
        new("CDL_MATCHING_LOW", "Matching Low", Bullish, 2, 58, (k, i) => k.Bear(i - 1) && k.Bear(i) && Math.Abs(k.C(i) - k.C(i - 1)) <= k.Equal(i) && k.DownTrend(i - 1)),
        new("CDL_IN_NECK", "In-Neck", Bearish, 2, 52, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Bull(i) && k.O(i) < k.L(i - 1) && k.C(i) >= k.C(i - 1) && k.C(i) <= k.C(i - 1) + k.Equal(i)),
        new("CDL_ON_NECK", "On-Neck", Bearish, 2, 52, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Bull(i) && k.O(i) < k.L(i - 1) && Math.Abs(k.C(i) - k.L(i - 1)) <= k.Equal(i)),
        new("CDL_THRUSTING", "Thrusting", Bearish, 2, 50, (k, i) => k.Bear(i - 1) && k.LongBody(i - 1) && k.Bull(i) && k.O(i) < k.L(i - 1) && k.C(i) > k.C(i - 1) + k.Equal(i) && k.C(i) <= k.C(i - 1) + 0.5m * k.Body(i - 1)),
        new("CDL_SEPARATING_LINES_BULL", "Bullish Separating Lines", Bullish, 2, 58, (k, i) => k.Bear(i - 1) && k.Bull(i) && Math.Abs(k.O(i) - k.O(i - 1)) <= k.Equal(i) && k.LongBody(i) && k.LoVeryShort(i) && k.UpTrend(i - 1)),
        new("CDL_SEPARATING_LINES_BEAR", "Bearish Separating Lines", Bearish, 2, 58, (k, i) => k.Bull(i - 1) && k.Bear(i) && Math.Abs(k.O(i) - k.O(i - 1)) <= k.Equal(i) && k.LongBody(i) && k.UpVeryShort(i) && k.DownTrend(i - 1)),
        new("CDL_HIKKAKE_BULL", "Bullish Hikkake", Bullish, 3, 62, (k, i) => k.Inside(i - 1) && k.H(i) < k.H(i - 1) && k.L(i) < k.L(i - 1), HikkakeLevels),
        new("CDL_HIKKAKE_BEAR", "Bearish Hikkake", Bearish, 3, 62, (k, i) => k.Inside(i - 1) && k.H(i) > k.H(i - 1) && k.L(i) > k.L(i - 1), HikkakeLevels),
        new("CDL_HIKKAKE_MOD_BULL", "Bullish Modified Hikkake", Bullish, 4, 64, (k, i) => k.Inside(i - 2) && k.Inside(i - 1) && k.H(i) < k.H(i - 1) && k.L(i) < k.L(i - 1), HikkakeLevels),
        new("CDL_HIKKAKE_MOD_BEAR", "Bearish Modified Hikkake", Bearish, 4, 64, (k, i) => k.Inside(i - 2) && k.Inside(i - 1) && k.H(i) > k.H(i - 1) && k.L(i) > k.L(i - 1), HikkakeLevels),

        // ---- three candles ----
        new("CDL_MORNING_STAR", "Morning Star", Bullish, 3, 75, (k, i) => StarBase(k, i, true) && k.SmallBody(i - 1) && !k.Doji(i - 1)),
        new("CDL_EVENING_STAR", "Evening Star", Bearish, 3, 75, (k, i) => StarBase(k, i, false) && k.SmallBody(i - 1) && !k.Doji(i - 1)),
        new("CDL_MORNING_DOJI_STAR", "Morning Doji Star", Bullish, 3, 76, (k, i) => StarBase(k, i, true) && k.Doji(i - 1)),
        new("CDL_EVENING_DOJI_STAR", "Evening Doji Star", Bearish, 3, 76, (k, i) => StarBase(k, i, false) && k.Doji(i - 1)),
        new("CDL_ABANDONED_BABY_BULL", "Bullish Abandoned Baby", Bullish, 3, 80, (k, i) => k.Bear(i - 2) && k.LongBody(i - 2) && k.Doji(i - 1) && k.H(i - 1) < k.L(i - 2) && k.Bull(i) && k.L(i) > k.H(i - 1) && k.C(i) > k.C(i - 2) + 0.3m * k.Body(i - 2)),
        new("CDL_ABANDONED_BABY_BEAR", "Bearish Abandoned Baby", Bearish, 3, 80, (k, i) => k.Bull(i - 2) && k.LongBody(i - 2) && k.Doji(i - 1) && k.L(i - 1) > k.H(i - 2) && k.Bear(i) && k.H(i) < k.L(i - 1) && k.C(i) < k.C(i - 2) - 0.3m * k.Body(i - 2)),
        new("CDL_THREE_WHITE_SOLDIERS", "Three White Soldiers", Bullish, 3, 75, (k, i) => SoldierBar(k, i - 2) && SoldierBar(k, i - 1) && SoldierBar(k, i) && RisingCloses(k, i - 2, i) && OpensInPrevBody(k, i - 2, i)),
        new("CDL_THREE_BLACK_CROWS", "Three Black Crows", Bearish, 3, 75, (k, i) => CrowBar(k, i - 2) && CrowBar(k, i - 1) && CrowBar(k, i) && FallingCloses(k, i - 2, i) && OpensInPrevBody(k, i - 2, i) && k.UpTrend(i - 2)),
        new("CDL_IDENTICAL_THREE_CROWS", "Identical Three Crows", Bearish, 3, 72, (k, i) => CrowBar(k, i - 2) && CrowBar(k, i - 1) && CrowBar(k, i) && FallingCloses(k, i - 2, i) && Math.Abs(k.O(i - 1) - k.C(i - 2)) <= k.Equal(i - 1) && Math.Abs(k.O(i) - k.C(i - 1)) <= k.Equal(i)),
        new("CDL_THREE_INSIDE_UP", "Three Inside Up", Bullish, 3, 66, (k, i) => HaramiBull(k, i - 1) && k.Bull(i) && k.C(i) > k.O(i - 2)),
        new("CDL_THREE_INSIDE_DOWN", "Three Inside Down", Bearish, 3, 66, (k, i) => HaramiBear(k, i - 1) && k.Bear(i) && k.C(i) < k.O(i - 2)),
        new("CDL_THREE_OUTSIDE_UP", "Three Outside Up", Bullish, 3, 70, (k, i) => EngulfBull(k, i - 1) && k.Bull(i) && k.C(i) > k.C(i - 1)),
        new("CDL_THREE_OUTSIDE_DOWN", "Three Outside Down", Bearish, 3, 70, (k, i) => EngulfBear(k, i - 1) && k.Bear(i) && k.C(i) < k.C(i - 1)),
        new("CDL_THREE_STARS_IN_SOUTH", "Three Stars in the South", Bullish, 3, 62, (k, i) =>
            k.Bear(i - 2) && k.LongBody(i - 2) && k.Lo(i - 2) > k.Body(i - 2) * 0.5m
            && k.Bear(i - 1) && k.Body(i - 1) < k.Body(i - 2) && k.O(i - 1) > k.C(i - 2) && k.O(i - 1) <= k.H(i - 2) && k.L(i - 1) > k.L(i - 2) && !k.LoVeryShort(i - 1)
            && k.Bear(i) && k.SmallBody(i) && k.UpVeryShort(i) && k.LoVeryShort(i) && k.H(i) <= k.H(i - 1) && k.L(i) >= k.L(i - 1)),
        new("CDL_ADVANCE_BLOCK", "Advance Block", Bearish, 3, 58, (k, i) =>
            k.Bull(i - 2) && k.Bull(i - 1) && k.Bull(i) && RisingCloses(k, i - 2, i) && OpensInPrevBody(k, i - 2, i)
            && k.Body(i) < k.Body(i - 1) && k.Body(i - 1) < k.Body(i - 2) && (k.Up(i) > k.Up(i - 2) || k.Up(i - 1) > k.Up(i - 2)) && k.UpTrend(i - 2)),
        new("CDL_STALLED_PATTERN", "Stalled Pattern", Bearish, 3, 56, (k, i) =>
            k.Bull(i - 2) && k.LongBody(i - 2) && k.Bull(i - 1) && k.LongBody(i - 1) && k.C(i - 1) > k.C(i - 2) && k.Up(i - 1) < 0.25m * k.Body(i - 1)
            && k.Bull(i) && k.SmallBody(i) && k.O(i) >= k.C(i - 1) - k.Body(i) - k.Near(i)),
        new("CDL_TWO_CROWS", "Two Crows", Bearish, 3, 60, (k, i) =>
            k.Bull(i - 2) && k.LongBody(i - 2) && k.Bear(i - 1) && k.BodyGapUp(i - 1) && k.Bear(i) && k.O(i) < k.O(i - 1) && k.O(i) > k.C(i - 1) && k.C(i) > k.O(i - 2) && k.C(i) < k.C(i - 2)),
        new("CDL_UPSIDE_GAP_TWO_CROWS", "Upside Gap Two Crows", Bearish, 3, 60, (k, i) =>
            k.Bull(i - 2) && k.LongBody(i - 2) && k.Bear(i - 1) && k.SmallBody(i - 1) && k.BodyGapUp(i - 1) && k.Bear(i) && k.O(i) > k.O(i - 1) && k.C(i) < k.C(i - 1) && k.C(i) > k.C(i - 2)),
        new("CDL_UNIQUE_THREE_RIVER", "Unique Three River", Bullish, 3, 60, (k, i) =>
            k.Bear(i - 2) && k.LongBody(i - 2) && k.Bear(i - 1) && k.C(i - 1) > k.C(i - 2) && k.O(i - 1) <= k.O(i - 2) && k.L(i - 1) < k.L(i - 2)
            && k.Bull(i) && k.SmallBody(i) && k.O(i) > k.L(i - 1) && k.C(i) < k.C(i - 1)),
        new("CDL_TRISTAR_BULL", "Bullish Tri-Star", Bullish, 3, 65, (k, i) => k.Doji(i - 2) && k.Doji(i - 1) && k.Doji(i) && k.Top(i - 1) < k.Bot(i - 2) && k.Bot(i) > k.Top(i - 1)),
        new("CDL_TRISTAR_BEAR", "Bearish Tri-Star", Bearish, 3, 65, (k, i) => k.Doji(i - 2) && k.Doji(i - 1) && k.Doji(i) && k.Bot(i - 1) > k.Top(i - 2) && k.Top(i) < k.Bot(i - 1)),
        new("CDL_STICK_SANDWICH", "Stick Sandwich", Bullish, 3, 58, (k, i) => k.Bear(i - 2) && k.Bull(i - 1) && k.L(i - 1) > k.C(i - 2) && k.Bear(i) && Math.Abs(k.C(i) - k.C(i - 2)) <= k.Equal(i)),
        new("CDL_TASUKI_GAP_UP", "Upside Tasuki Gap", Bullish, 3, 60, (k, i) =>
            k.Bull(i - 2) && k.Bull(i - 1) && k.BodyGapUp(i - 1) && k.Bear(i) && k.O(i) < k.C(i - 1) && k.O(i) > k.O(i - 1) && k.C(i) < k.O(i - 1) && k.C(i) > k.Top(i - 2) && Math.Abs(k.Body(i - 1) - k.Body(i)) < k.Near(i)),
        new("CDL_TASUKI_GAP_DOWN", "Downside Tasuki Gap", Bearish, 3, 60, (k, i) =>
            k.Bear(i - 2) && k.Bear(i - 1) && k.BodyGapDown(i - 1) && k.Bull(i) && k.O(i) > k.C(i - 1) && k.O(i) < k.O(i - 1) && k.C(i) > k.O(i - 1) && k.C(i) < k.Bot(i - 2) && Math.Abs(k.Body(i - 1) - k.Body(i)) < k.Near(i)),
        new("CDL_GAP_SIDE_SIDE_WHITE_UP", "Upside Gap Side-by-Side White Lines", Bullish, 3, 60, (k, i) =>
            k.Bot(i - 1) > k.Top(i - 2) && k.Bot(i) > k.Top(i - 2) && k.Bull(i - 1) && k.Bull(i) && Math.Abs(k.Body(i) - k.Body(i - 1)) <= k.Near(i) && Math.Abs(k.O(i) - k.O(i - 1)) <= k.Near(i)),
        new("CDL_GAP_SIDE_SIDE_WHITE_DOWN", "Downside Gap Side-by-Side White Lines", Bearish, 3, 60, (k, i) =>
            k.Top(i - 1) < k.Bot(i - 2) && k.Top(i) < k.Bot(i - 2) && k.Bull(i - 1) && k.Bull(i) && Math.Abs(k.Body(i) - k.Body(i - 1)) <= k.Near(i) && Math.Abs(k.O(i) - k.O(i - 1)) <= k.Near(i)),
        new("CDL_XSIDE_GAP_THREE_METHODS_UP", "Upside Gap Three Methods", Bullish, 3, 58, (k, i) =>
            k.Bull(i - 2) && k.Bull(i - 1) && k.BodyGapUp(i - 1) && k.Bear(i) && k.O(i) < k.C(i - 1) && k.O(i) > k.O(i - 1) && k.C(i) < k.O(i - 1) && k.C(i) > k.O(i - 2)),
        new("CDL_XSIDE_GAP_THREE_METHODS_DOWN", "Downside Gap Three Methods", Bearish, 3, 58, (k, i) =>
            k.Bear(i - 2) && k.Bear(i - 1) && k.BodyGapDown(i - 1) && k.Bull(i) && k.O(i) > k.C(i - 1) && k.O(i) < k.O(i - 1) && k.C(i) > k.O(i - 1) && k.C(i) < k.O(i - 2)),
        new("CDL_THREE_LINE_STRIKE_BULL", "Bullish Three-Line Strike", Bullish, 4, 64, (k, i) =>
            k.Bull(i - 3) && k.Bull(i - 2) && k.Bull(i - 1) && RisingCloses(k, i - 3, i - 1) && k.Bear(i) && k.O(i) > k.C(i - 1) && k.C(i) < k.O(i - 3)),
        new("CDL_THREE_LINE_STRIKE_BEAR", "Bearish Three-Line Strike", Bearish, 4, 64, (k, i) =>
            k.Bear(i - 3) && k.Bear(i - 2) && k.Bear(i - 1) && FallingCloses(k, i - 3, i - 1) && k.Bull(i) && k.O(i) < k.C(i - 1) && k.C(i) > k.O(i - 3)),

        // ---- four / five candles ----
        new("CDL_CONCEALING_BABY_SWALLOW", "Concealing Baby Swallow", Bullish, 4, 62, (k, i) =>
            k.Bear(i - 3) && k.Marubozu(i - 3) && k.Bear(i - 2) && k.Marubozu(i - 2)
            && k.Bear(i - 1) && k.O(i - 1) < k.C(i - 2) && k.H(i - 1) > k.C(i - 2)
            && k.Bear(i) && k.O(i) > k.H(i - 1) && k.C(i) < k.L(i - 1)),
        new("CDL_BREAKAWAY_BULL", "Bullish Breakaway", Bullish, 5, 64, (k, i) =>
            k.Bear(i - 4) && k.LongBody(i - 4) && k.Bear(i - 3) && k.Top(i - 3) < k.Bot(i - 4)
            && k.H(i - 2) < k.H(i - 3) && k.L(i - 2) < k.L(i - 3) && k.H(i - 1) < k.H(i - 2) && k.L(i - 1) < k.L(i - 2) && k.Bear(i - 1)
            && k.Bull(i) && k.C(i) > k.O(i - 3) && k.C(i) < k.C(i - 4)),
        new("CDL_BREAKAWAY_BEAR", "Bearish Breakaway", Bearish, 5, 64, (k, i) =>
            k.Bull(i - 4) && k.LongBody(i - 4) && k.Bull(i - 3) && k.Bot(i - 3) > k.Top(i - 4)
            && k.H(i - 2) > k.H(i - 3) && k.L(i - 2) > k.L(i - 3) && k.H(i - 1) > k.H(i - 2) && k.L(i - 1) > k.L(i - 2) && k.Bull(i - 1)
            && k.Bear(i) && k.C(i) < k.O(i - 3) && k.C(i) > k.C(i - 4)),
        new("CDL_LADDER_BOTTOM", "Ladder Bottom", Bullish, 5, 64, (k, i) =>
            k.Bear(i - 4) && k.Bear(i - 3) && k.Bear(i - 2) && FallingCloses(k, i - 4, i - 2) && k.O(i - 3) < k.O(i - 4) && k.O(i - 2) < k.O(i - 3)
            && k.Bear(i - 1) && k.Up(i - 1) > k.Body(i - 1)
            && k.Bull(i) && k.O(i) > k.Top(i - 1) && k.C(i) > k.H(i - 1)),
        new("CDL_MAT_HOLD", "Mat Hold", Bullish, 5, 66, (k, i) =>
            k.Bull(i - 4) && k.LongBody(i - 4) && k.Bear(i - 3) && k.SmallBody(i - 3) && k.BodyGapUp(i - 3)
            && k.SmallBody(i - 2) && k.SmallBody(i - 1)
            && Math.Min(k.Bot(i - 3), Math.Min(k.Bot(i - 2), k.Bot(i - 1))) > k.C(i - 4) - 0.5m * k.Body(i - 4)
            && k.Bull(i) && k.O(i) > k.C(i - 1) && k.C(i) > Math.Max(k.H(i - 3), Math.Max(k.H(i - 2), k.H(i - 1)))),
        new("CDL_RISE_THREE_METHODS", "Rising Three Methods", Bullish, 5, 68, (k, i) =>
            k.Bull(i - 4) && k.LongBody(i - 4)
            && Enumerable.Range(i - 3, 3).All(j => k.SmallBody(j) && k.H(j) <= k.H(i - 4) && k.L(j) >= k.L(i - 4))
            && FallingCloses(k, i - 3, i - 1)
            && k.Bull(i) && k.LongBody(i) && k.O(i) > k.C(i - 1) && k.C(i) > k.C(i - 4)),
        new("CDL_FALL_THREE_METHODS", "Falling Three Methods", Bearish, 5, 68, (k, i) =>
            k.Bear(i - 4) && k.LongBody(i - 4)
            && Enumerable.Range(i - 3, 3).All(j => k.SmallBody(j) && k.H(j) <= k.H(i - 4) && k.L(j) >= k.L(i - 4))
            && RisingCloses(k, i - 3, i - 1)
            && k.Bear(i) && k.LongBody(i) && k.O(i) < k.C(i - 1) && k.C(i) < k.C(i - 4)),
    ];
}
