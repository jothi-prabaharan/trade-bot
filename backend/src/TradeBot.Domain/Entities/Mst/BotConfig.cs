using TradeBot.Domain.Common;
using TradeBot.Domain.Enums;

namespace TradeBot.Domain.Entities.Mst;

public class GeminiNewsConfig : AuditableEntity
{
    public long GeminiConfigId { get; set; }
    [Encrypted] public string? GeminiApiKey { get; set; }
    public required string ModelName { get; set; }
    public bool UseSearchGrounding { get; set; } = true;
    public TimeOnly PremarketScanTime { get; set; } = new(8, 30);
    public int IntradayPollSeconds { get; set; } = 60;
    public decimal VolumeSpikeTriggerMultiplier { get; set; } = 2.5m;
    public decimal PatternWeightPct { get; set; } = 50m;
    public decimal NewsWeightPct { get; set; } = 30m;
    public decimal OiGreeksWeightPct { get; set; } = 20m;
    public ImpactLevel VetoMinImpactLevel { get; set; } = ImpactLevel.High;
    public bool AutoPauseOnExtremeMacro { get; set; } = true;
    public int MaxCallsPerMinute { get; set; } = 30;
    public bool IsActive { get; set; } = true;
}

/// <summary>Global signal-generation settings (signals are shared by all accounts).</summary>
public class StrategyConfig : AuditableEntity
{
    public long StrategyConfigId { get; set; }
    public StrikeSelection StrikeSelection { get; set; } = StrikeSelection.Atm;
    public ExpiryPolicy ExpiryPolicy { get; set; } = ExpiryPolicy.NearestSkipExpiryDay;
    public ProductType Product { get; set; } = ProductType.Intraday;
    public Timeframe TrendTimeframe { get; set; } = Timeframe.M15;
    public Timeframe TriggerTimeframe { get; set; } = Timeframe.M5;
    public bool RequireOptionConfirmation { get; set; } = true;
    public decimal MinCombinedConfidence { get; set; } = 70m;
    public TimeOnly EntryStartTime { get; set; } = new(9, 15);
    public TimeOnly EntryCutoffTime { get; set; } = new(14, 45);
    public TimeOnly SquareOffTime { get; set; } = new(15, 15);
    public TimeOnly AuthLoginTime { get; set; } = new(8, 45);
    public int AuthMaxRetries { get; set; } = 3;
    public bool IsActive { get; set; } = true;
}

public class AlertChannel : AuditableEntity
{
    public long AlertChannelId { get; set; }
    public AlertChannelType ChannelType { get; set; } = AlertChannelType.Telegram;
    public required string ChannelName { get; set; }
    [Encrypted] public string? BotToken { get; set; }
    public string? ChatId { get; set; }
    public bool NotifyOnEntry { get; set; } = true;
    public bool NotifyOnExit { get; set; } = true;
    public bool NotifyOnCritical { get; set; } = true;
    public bool IsActive { get; set; } = true;
}
