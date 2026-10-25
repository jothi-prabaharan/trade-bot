using System.Text.Json.Nodes;
using TradeBot.Application.Market;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Patterns;

/// <summary>Code-side definition of a pattern; mst.chart_patterns is seeded from these and can override them.</summary>
public sealed record PatternDefinition(
    string Code, string Name, PatternCategory Category, PatternDirection Direction, int MinCandles,
    decimal DefaultWeight, bool RequireVolumeConfirm, bool RequireBreakoutClose, JsonObject DefaultParameters);

public sealed record Pivot(int Index, DateTime Time, decimal Price, bool IsHigh);

/// <summary>Breakout level as a line: value at detection plus slope per bar since detection.</summary>
public readonly record struct LevelLine(decimal AtDetection, decimal SlopePerBar = 0)
{
    public decimal At(int barsSinceDetection) => AtDetection + SlopePerBar * barsSinceDetection;
    public static implicit operator LevelLine(decimal flat) => new(flat);
}

/// <summary>A pattern that has formed and waits for its breakout (or is already confirmed by its own bar).</summary>
public sealed record PatternSetup(
    string Code, PatternDirection Direction, LevelLine Breakout, decimal StructuralSl, decimal StructuralTarget,
    decimal Quality, IReadOnlyList<Pivot> Pivots, string Key, bool ConfirmedOnDetection = false);

public sealed record ConfirmedPattern(
    string Code, PatternCategory Category, PatternDirection Direction, Timeframe Timeframe,
    decimal BreakoutPrice, decimal EntryReference, decimal StructuralSl, decimal StructuralTarget,
    decimal Confidence, decimal VolumeRatio, MarketBias TrendBias, IReadOnlyList<Pivot> Pivots,
    DateTime DetectedBarTime, DateTime ConfirmedBarTime);

/// <summary>Effective parameters: catalog JSON overrides merged onto the definition defaults.</summary>
public sealed class PatternParams(JsonObject defaults, JsonObject? overrides = null)
{
    public decimal this[string key] => (overrides?[key] ?? defaults[key]) is { } node
        ? decimal.Parse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
        : throw new KeyNotFoundException($"Pattern parameter '{key}' has no default.");

    public int Int(string key) => (int)this[key];

    public decimal Get(string key, decimal fallback) => (overrides?[key] ?? defaults[key]) is null ? fallback : this[key];
}

/// <summary>Per-bar-close scratch shared by all detectors: ATR, candle kit and cached pivots.</summary>
public sealed class PatternScratch(IReadOnlyList<Bar> bars)
{
    private decimal[]? _atr;
    private CandleKit? _kit;

    public decimal[] Atr => _atr ??= Indicators.Atr(bars);
    public CandleKit Kit => _kit ??= new CandleKit(bars);
    public Dictionary<string, object> Cache { get; } = new();
}

public sealed class PatternContext(IReadOnlyList<Bar> bars, PatternParams parameters, PatternScratch? scratch = null)
{
    private readonly PatternScratch _scratch = scratch ?? new PatternScratch(bars);

    public IReadOnlyList<Bar> Bars { get; } = bars;
    public PatternParams P { get; } = parameters;
    public int Last => Bars.Count - 1;
    public decimal[] Atr => _scratch.Atr;
    public decimal AtrAt(int i) => Math.Max(Atr[i], Bars[i].Close * 0.0005m); // floor avoids zero-range data
    public CandleKit Kit => _scratch.Kit;
    public Dictionary<string, object> Cache => _scratch.Cache;
}

public interface IPatternDetector
{
    PatternDefinition Definition { get; }

    /// <summary>Setups that complete on the last bar of <see cref="PatternContext.Bars"/>.</summary>
    IEnumerable<PatternSetup> Detect(PatternContext ctx);
}

internal static class SetupMath
{
    /// <summary>Builds a directional setup with risk-multiple target; null when levels are degenerate.</summary>
    public static PatternSetup? RiskMultiple(string code, PatternDirection dir, decimal breakout, decimal sl, decimal rr, decimal quality, IReadOnlyList<Pivot> pivots, string key, bool confirmedOnDetection = false)
    {
        var risk = dir == PatternDirection.Bullish ? breakout - sl : sl - breakout;
        if (risk <= 0) return null;
        var target = dir == PatternDirection.Bullish ? breakout + rr * risk : breakout - rr * risk;
        return target <= 0 ? null : new PatternSetup(code, dir, breakout, sl, target, quality, pivots, key, confirmedOnDetection);
    }

    public static PatternSetup? Measured(string code, PatternDirection dir, LevelLine breakout, decimal sl, decimal target, decimal quality, IReadOnlyList<Pivot> pivots, string key, bool confirmedOnDetection = false)
    {
        var b = breakout.AtDetection;
        var ok = dir == PatternDirection.Bullish ? sl < b && b < target : target < b && b < sl;
        return ok && target > 0 ? new PatternSetup(code, dir, breakout, sl, target, quality, pivots, key, confirmedOnDetection) : null;
    }

    public static decimal Clamp01(decimal v) => Math.Clamp(v, 0, 1);
}
