using TradeBot.Domain.Common;
using TradeBot.Domain.Enums;

namespace TradeBot.Domain.Entities.Mst;

public class TradingAccount : AuditableEntity
{
    public long AccountId { get; set; }
    public required string AccountName { get; set; }
    public required string ClientId { get; set; }
    public required string UserName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? BrokerBranch { get; set; }
    public TradeMode TradeMode { get; set; } = TradeMode.Paper;
    public bool IsDataProvider { get; set; }
    public string? LoginId { get; set; }
    [Encrypted] public string? LoginPassword { get; set; }
    [Encrypted] public string? LoginPin { get; set; }
    [Encrypted] public string? ApiKey { get; set; }
    [Encrypted] public string? ApiSecret { get; set; }
    [Encrypted] public string? TotpSecret { get; set; }
    public DateTime? RealModeEnabledAt { get; set; }
    public string? RealModeEnabledBy { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }

    public AccountRiskConfig? RiskConfig { get; set; }
    public ICollection<AccountAuthToken> AuthTokens { get; set; } = [];
}

public class AccountAuthToken
{
    public long TokenId { get; set; }
    public long AccountId { get; set; }
    [Encrypted] public string? XJwtToken { get; set; }
    [Encrypted] public string? PublicAccessToken { get; set; }
    [Encrypted] public string? ReadAccessToken { get; set; }
    public DateTime? TokenExpiry { get; set; }
    public LoginStatus LoginStatus { get; set; } = LoginStatus.Pending;
    public LoginMethod LoginMethod { get; set; } = LoginMethod.Headless;
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }

    public TradingAccount Account { get; set; } = null!;
}

public class AccountRiskConfig : AuditableEntity
{
    public long ConfigId { get; set; }
    public long AccountId { get; set; }
    public decimal AllocatedCapital { get; set; } = 100000m;
    public decimal OpeningBalance { get; set; } = 100000m;
    public decimal AvailableMargin { get; set; } = 100000m;
    public decimal UtilisedMargin { get; set; }
    public decimal RiskPerTradePct { get; set; } = 2m;
    public decimal MinSingleTradePrice { get; set; } = 30m;
    public decimal MaxSingleTradePrice { get; set; } = 400m;
    public decimal DefaultSlPct { get; set; } = 15m;
    public decimal DefaultTargetPct { get; set; } = 30m;
    public decimal MinRiskRewardRatio { get; set; } = 2m;
    public decimal MaxSlPct { get; set; } = 25m;
    public decimal BreakevenTriggerPct { get; set; } = 50m;
    public decimal TrailingSlStepPct { get; set; } = 10m;
    public decimal MaxDailyLoss { get; set; } = 3000m;
    public decimal MaxDailyProfit { get; set; } = 6000m;
    public int MaxOpenTrades { get; set; } = 1;
    public int MaxLotsPerTrade { get; set; } = 10;
    public int MaxTradesPerDay { get; set; } = 3;
    public int ReentryCooldownMinutes { get; set; } = 15;
    public decimal LotMultiplier { get; set; } = 1m;
    public decimal MinPatternConfidence { get; set; } = 70m;
    public bool BlockTradeOnOpposingNews { get; set; } = true;
    public decimal SlippageBufferPct { get; set; } = 0.5m;
    public decimal SimulatedSlippagePct { get; set; } = 0.25m;
    public decimal SimulatedBrokeragePerOrder { get; set; } = 20m;
    public DateTime? FundsSyncedAt { get; set; }
    public uint Version { get; set; }

    public TradingAccount Account { get; set; } = null!;
}
