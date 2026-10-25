using TradeBot.Domain.Common;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Domain.Entities.Trn;

public class CompanyDailyNews
{
    public long NewsId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long? CompanyId { get; set; }
    public long? InstrumentId { get; set; }
    public NewsScanType ScanType { get; set; }
    public required string Headline { get; set; }
    public string? Summary { get; set; }
    public string SourceUrls { get; set; } = "[]";
    public NewsCategory NewsCategory { get; set; }
    public MarketBias Sentiment { get; set; }
    public int SentimentScore { get; set; }
    public ImpactLevel ImpactLevel { get; set; }
    public decimal NiftyWeightedImpact { get; set; }
    public decimal BankniftyWeightedImpact { get; set; }
    public required string ContentHash { get; set; }
    public string GeminiStructuredOutput { get; set; } = "{}";
    public DateTime? PublishedAt { get; set; }
    public DateTime AnalyzedAt { get; set; }

    public StockCompany? Company { get; set; }
    public Instrument? Instrument { get; set; }
}

public class DailyMarketSentiment : IHasUpdatedAt
{
    public long SentimentId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long InstrumentId { get; set; }
    public decimal NetSentimentScore { get; set; }
    public MarketBias DominantBias { get; set; } = MarketBias.Neutral;
    public string ActiveCatalysts { get; set; } = "[]";
    public bool TradingHaltActive { get; set; }
    public string? HaltReason { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Instrument Instrument { get; set; } = null!;
}

public class OhlcvCandle
{
    public long CandleId { get; set; }
    public long InstrumentId { get; set; }
    public long? SecurityId { get; set; }
    public Timeframe Timeframe { get; set; }
    public DateTime CandleTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public long? OpenInterest { get; set; }

    public Instrument Instrument { get; set; } = null!;
    public SecurityMaster? Security { get; set; }
}

public class DetectedPattern
{
    public long DetectionId { get; set; }
    public long InstrumentId { get; set; }
    public long? SecurityId { get; set; }
    public long PatternId { get; set; }
    public Timeframe Timeframe { get; set; }
    public Timeframe? TrendTimeframe { get; set; }
    public MarketBias? TrendBias { get; set; }
    public PatternDirection Direction { get; set; }
    public decimal NeckOrBreakoutPrice { get; set; }
    public decimal StructuralSlPrice { get; set; }
    public decimal StructuralTargetPrice { get; set; }
    public string PivotCoordinates { get; set; } = "[]";
    public decimal ConfidenceScore { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime? BreakoutCandleTime { get; set; }
    public DateTime DetectedAt { get; set; }

    public Instrument Instrument { get; set; } = null!;
    public SecurityMaster? Security { get; set; }
    public ChartPattern Pattern { get; set; } = null!;
}

public class Signal
{
    public long SignalId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long DetectionId { get; set; }
    public long? NewsId { get; set; }
    public long InstrumentId { get; set; }
    public long SecurityId { get; set; }
    public SignalType SignalType { get; set; } = SignalType.Buy;
    public decimal SpotPrice { get; set; }
    public decimal? OptionDelta { get; set; }
    public decimal EntryOptionLtp { get; set; }
    public decimal MandatorySlPrice { get; set; }
    public decimal MandatoryTargetPrice { get; set; }
    public decimal RiskRewardRatio { get; private set; }
    public SlMethod SlMethod { get; set; }
    public decimal PatternScore { get; set; }
    public decimal NewsScore { get; set; }
    public decimal OiGreeksScore { get; set; }
    public decimal CombinedAiConfidence { get; set; }
    public string GreeksAndOi { get; set; } = "{}";
    public SignalStatus Status { get; set; } = SignalStatus.New;
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }

    public DetectedPattern Detection { get; set; } = null!;
    public CompanyDailyNews? News { get; set; }
    public Instrument Instrument { get; set; } = null!;
    public SecurityMaster Security { get; set; } = null!;
}

public class DailyWatchlist
{
    public long WatchlistId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long InstrumentId { get; set; }
    public WatchlistSource Source { get; set; }
    public decimal Score { get; set; }
    public string? Reason { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime AddedAt { get; set; }

    public Instrument Instrument { get; set; } = null!;
}
