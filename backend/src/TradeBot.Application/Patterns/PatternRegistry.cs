using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Patterns;

/// <summary>Every detector the engine knows; mst.chart_patterns rows are matched by code.</summary>
public static class PatternRegistry
{
    public static IReadOnlyList<IPatternDetector> All { get; } =
    [
        .. CandlestickCatalog.All.Select(s => new CandlestickDetector(s)),
        .. ClassicalCatalog.All,
        .. HarmonicCatalog.All,
        .. SmcCatalog.All,
    ];

    public static IReadOnlyDictionary<string, IPatternDetector> ByCode { get; } = All.ToDictionary(d => d.Definition.Code);

    /// <summary>Phase 0: candlestick and classical families start active; harmonic and SMC are seeded inactive.</summary>
    public static bool ActiveByDefault(PatternCategory category) => category is PatternCategory.Candlestick or PatternCategory.Classical;

    public static ChartPattern ToCatalogRow(PatternDefinition d) => new()
    {
        PatternCode = d.Code,
        PatternName = d.Name,
        Category = d.Category,
        Direction = d.Direction,
        MinCandlesRequired = d.MinCandles,
        DefaultWeight = d.DefaultWeight,
        RequireVolumeConfirm = d.RequireVolumeConfirm,
        RequireBreakoutClose = d.RequireBreakoutClose,
        PatternParameters = d.DefaultParameters.ToJsonString(),
        IsActive = ActiveByDefault(d.Category),
    };
}
