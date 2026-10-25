using System.Text.Json.Nodes;
using TradeBot.Application.Market;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Patterns;

/// <summary>
/// Runs active detectors on each closed bar, holds setups until a confirming close (with volume where required)
/// and scores confirmed patterns 0-100. One instance per engine; call from a single thread.
/// </summary>
public sealed class PatternEngine
{
    public const decimal DefaultVolumeFactor = 1.2m;
    private const int MaxRememberedKeys = 5000;

    private sealed record Active(IPatternDetector Detector, ChartPattern Row, PatternParams Params);
    private sealed record Pending(PatternSetup Setup, Active Pattern, DateTime DetectedBarTime);

    private IReadOnlyList<Active> _active = [];
    private readonly Dictionary<(SeriesKey, Timeframe), List<Pending>> _pending = new();
    private readonly Dictionary<(SeriesKey, Timeframe), Dictionary<string, DateTime>> _seen = new();

    public PatternEngine(IEnumerable<ChartPattern> catalog) => Reload(catalog);

    public int ActiveCount => _active.Count;

    /// <summary>Applies mst.chart_patterns changes (toggles, weights, parameter overrides). Unknown codes are ignored.</summary>
    public void Reload(IEnumerable<ChartPattern> catalog) =>
        _active = catalog
            .Where(r => r.IsActive && PatternRegistry.ByCode.ContainsKey(r.PatternCode))
            .Select(r =>
            {
                var d = PatternRegistry.ByCode[r.PatternCode];
                var overrides = string.IsNullOrWhiteSpace(r.PatternParameters) ? null : JsonNode.Parse(r.PatternParameters) as JsonObject;
                return new Active(d, r, new PatternParams(d.Definition.DefaultParameters, overrides));
            })
            .ToList();

    public IReadOnlyList<ConfirmedPattern> OnBarClosed(SeriesKey series, Timeframe tf, IReadOnlyList<Bar> bars, MarketBias trendBias)
    {
        if (bars.Count < 2) return [];
        var key = (series, tf);
        var pending = _pending.TryGetValue(key, out var p) ? p : _pending[key] = [];
        var seen = _seen.TryGetValue(key, out var s) ? s : _seen[key] = [];
        var bar = bars[^1];
        var confirmed = new List<ConfirmedPattern>();

        // 1) Resolve setups waiting for a breakout
        foreach (var w in pending.ToList())
        {
            var detectedIdx = IndexOf(bars, w.DetectedBarTime);
            var since = detectedIdx < 0 ? int.MaxValue : bars.Count - 1 - detectedIdx;
            var bull = w.Setup.Direction == PatternDirection.Bullish;
            var invalid = bull ? bar.Close < w.Setup.StructuralSl : bar.Close > w.Setup.StructuralSl;
            if (since > w.Pattern.Params.Int("confirmWithinBars") || invalid)
            {
                pending.Remove(w);
                continue;
            }

            var level = w.Setup.Breakout.At(since);
            if (bull ? bar.Close <= level : bar.Close >= level) continue;
            if (Confirm(w.Pattern, w.Setup, bars, level, w.DetectedBarTime, tf, trendBias) is { } c) confirmed.Add(c);
            if (VolumeOk(w.Pattern, bars)) pending.Remove(w);
        }

        // 2) New setups completing on this bar
        var scratch = new PatternScratch(bars);
        foreach (var a in _active)
        {
            if (bars.Count < a.Row.MinCandlesRequired) continue;
            IEnumerable<PatternSetup> setups;
            try { setups = a.Detector.Detect(new PatternContext(bars, a.Params, scratch)).ToList(); }
            catch (Exception) { continue; } // a faulty detector must not stop the others

            foreach (var setup in setups)
            {
                if (!seen.TryAdd(setup.Key, bar.Time)) continue;
                if (setup.ConfirmedOnDetection || !a.Row.RequireBreakoutClose)
                {
                    var level = setup.ConfirmedOnDetection ? setup.Breakout.AtDetection : bar.Close;
                    if (Confirm(a, setup, bars, level, bar.Time, tf, trendBias) is { } c) confirmed.Add(c);
                }
                else pending.Add(new Pending(setup, a, bar.Time));
            }
        }

        if (seen.Count > MaxRememberedKeys)
            foreach (var old in seen.OrderBy(x => x.Value).Take(seen.Count - MaxRememberedKeys).ToList()) seen.Remove(old.Key);
        return confirmed;
    }

    public static decimal Score(decimal weight, decimal quality, PatternDirection dir, MarketBias trend, decimal volumeRatio, bool hasVolume)
    {
        var aligned = (dir == PatternDirection.Bullish && trend == MarketBias.Bullish) || (dir == PatternDirection.Bearish && trend == MarketBias.Bearish);
        var opposed = (dir == PatternDirection.Bullish && trend == MarketBias.Bearish) || (dir == PatternDirection.Bearish && trend == MarketBias.Bullish);
        var score = weight * (0.6m + 0.4m * SetupMath.Clamp01(quality))
            + (aligned ? 10 : opposed ? -15 : 0)
            + (hasVolume ? Math.Clamp((volumeRatio - 1) * 10, -5, 10) : 0);
        return Math.Round(Math.Clamp(score, 0, 100), 2);
    }

    private static ConfirmedPattern? Confirm(Active a, PatternSetup s, IReadOnlyList<Bar> bars, decimal level, DateTime detectedAt, Timeframe tf, MarketBias trend)
    {
        if (!VolumeOk(a, bars)) return null;
        var bar = bars[^1];
        var bull = s.Direction == PatternDirection.Bullish;
        var entry = bar.Close;
        if (bull ? !(s.StructuralSl < entry && entry < s.StructuralTarget) : !(s.StructuralTarget < entry && entry < s.StructuralSl)) return null;

        var (ratio, hasVolume) = VolumeRatio(bars);
        return new ConfirmedPattern(
            s.Code, a.Detector.Definition.Category, s.Direction, tf, level, entry, s.StructuralSl, s.StructuralTarget,
            Score(a.Row.DefaultWeight, s.Quality, s.Direction, trend, ratio, hasVolume), Math.Round(ratio, 2), trend,
            s.Pivots, detectedAt, bar.Time);
    }

    private static bool VolumeOk(Active a, IReadOnlyList<Bar> bars)
    {
        if (!a.Row.RequireVolumeConfirm) return true;
        var (ratio, hasVolume) = VolumeRatio(bars);
        return !hasVolume || ratio >= a.Params.Get("volumeFactor", DefaultVolumeFactor); // index-only series carry no volume
    }

    private static (decimal Ratio, bool HasVolume) VolumeRatio(IReadOnlyList<Bar> bars)
    {
        var avg = Indicators.AverageVolume(bars, bars.Count - 1);
        return avg > 0 ? (bars[^1].Volume / avg, true) : (1, false);
    }

    private static int IndexOf(IReadOnlyList<Bar> bars, DateTime time)
    {
        int lo = 0, hi = bars.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var cmp = bars[mid].Time.CompareTo(time);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }
}
