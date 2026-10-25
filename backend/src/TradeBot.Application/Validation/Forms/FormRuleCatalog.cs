using TradeBot.Domain.Enums;

namespace TradeBot.Application.Validation.Forms;

public enum FieldType { Number, Integer, String, Boolean, Time, Date, Enum, Json }

public sealed record FieldRule(
    string Field, FieldType Type, bool Required = true,
    decimal? Min = null, bool MinExclusive = false, decimal? Max = null, bool MaxExclusive = false,
    int? Scale = null, int? MaxLength = null, string? Pattern = null, IReadOnlyList<string>? AllowedValues = null);

/// <summary>Evaluated only when none of its fields has a field-level violation (same semantics in Angular).</summary>
public sealed record CrossFieldRule(string Code, IReadOnlyList<string> Fields, string Message);

public sealed record FormRuleSet(string Form, IReadOnlyList<FieldRule> Fields, IReadOnlyList<CrossFieldRule> CrossFieldRules);

public static class FormCodes
{
    public const string Required = "FIELD_REQUIRED";
    public const string Type = "FIELD_TYPE";
    public const string Min = "FIELD_MIN";
    public const string Max = "FIELD_MAX";
    public const string Scale = "FIELD_SCALE";
    public const string MaxLength = "FIELD_MAX_LENGTH";
    public const string Pattern = "FIELD_PATTERN";
    public const string Enum = "FIELD_ENUM";

    public const string PremiumBand = "CX_PREMIUM_BAND";
    public const string MaxSlGteDefaultSl = "CX_MAX_SL_GTE_DEFAULT_SL";
    public const string FallbackRr = "CX_FALLBACK_RR";
    public const string DailyLossLteCapital = "CX_DAILY_LOSS_LTE_CAPITAL";
    public const string DataProviderCredentials = "CX_DATA_PROVIDER_CREDENTIALS";
    public const string WeightsSum100 = "CX_WEIGHTS_SUM_100";
    public const string PremarketBeforeOpen = "CX_PREMARKET_BEFORE_OPEN";
    public const string SessionTimes = "CX_SESSION_TIMES";
    public const string AuthBeforeEntry = "CX_AUTH_BEFORE_ENTRY";
    public const string TrendAboveTrigger = "CX_TREND_TF_ABOVE_TRIGGER";
    public const string RealConfirmation = "CX_REAL_CONFIRMATION";
    public const string SpecialSessionTimes = "CX_SPECIAL_SESSION_TIMES";
}

/// <summary>
/// Fixed sanity bounds for every config drawer. Must match the DB CHECK constraints exactly;
/// the parity test in TradeBot.Infrastructure.Tests enforces this for risk config.
/// </summary>
public static class FormRuleCatalog
{
    public const string RiskConfig = "riskConfig";
    public const string TradingAccount = "tradingAccount";
    public const string GeminiConfig = "geminiConfig";
    public const string StrategyConfig = "strategyConfig";
    public const string ChartPattern = "chartPattern";
    public const string ModeToggle = "modeToggle";
    public const string MarketHoliday = "marketHoliday";

    private const string ClientIdPattern = "^[A-Za-z0-9_-]+$";

    public static readonly IReadOnlyDictionary<string, FormRuleSet> Forms = new[]
    {
        new FormRuleSet(RiskConfig,
        [
            Money("allocatedCapital", min: 0, minExclusive: true),
            Money("openingBalance", min: 0),
            Money("availableMargin", min: 0),
            Money("utilisedMargin", min: 0),
            Pct("riskPerTradePct", min: 0, minExclusive: true, max: 10),
            Money("minSingleTradePrice", min: 0, minExclusive: true),
            Money("maxSingleTradePrice", min: 0, minExclusive: true),
            Pct("defaultSlPct", min: 0, minExclusive: true, max: 100, maxExclusive: true),
            Pct("defaultTargetPct", min: 0, minExclusive: true, max: 999.9999m),
            new("minRiskRewardRatio", FieldType.Number, Min: 1, Max: 9999.99m, Scale: 2),
            Pct("maxSlPct", min: 0, minExclusive: true, max: 100, maxExclusive: true),
            Pct("breakevenTriggerPct", min: 0, minExclusive: true, max: 100),
            Pct("trailingSlStepPct", min: 0, minExclusive: true, max: 100, maxExclusive: true),
            Money("maxDailyLoss", min: 0, minExclusive: true),
            Money("maxDailyProfit", min: 0, minExclusive: true),
            new("maxOpenTrades", FieldType.Integer, Min: 1),
            new("maxLotsPerTrade", FieldType.Integer, Min: 1),
            new("lotMultiplier", FieldType.Number, Min: 0, MinExclusive: true, Max: 1, Scale: 2),
            Score("minPatternConfidence"),
            new("blockTradeOnOpposingNews", FieldType.Boolean),
            Pct("slippageBufferPct", min: 0, max: 5),
            Pct("simulatedSlippagePct", min: 0, max: 5),
            Money("simulatedBrokeragePerOrder", min: 0),
        ],
        [
            new(FormCodes.PremiumBand, ["minSingleTradePrice", "maxSingleTradePrice"], "Max premium must be greater than min premium."),
            new(FormCodes.MaxSlGteDefaultSl, ["defaultSlPct", "maxSlPct"], "Max SL % must be at least the default SL %."),
            new(FormCodes.FallbackRr, ["defaultSlPct", "defaultTargetPct", "minRiskRewardRatio"], "Default target % must be at least default SL % × min risk-reward."),
            new(FormCodes.DailyLossLteCapital, ["maxDailyLoss", "allocatedCapital"], "Max daily loss cannot exceed allocated capital."),
        ]),

        new FormRuleSet(TradingAccount,
        [
            Str("accountName", 100),
            Str("clientId", 50, pattern: ClientIdPattern),
            Str("userName", 150),
            Str("email", 254, required: false, pattern: @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
            Str("phone", 20, required: false, pattern: "^[6-9][0-9]{9}$"),
            Str("brokerBranch", 100, required: false),
            Str("loginId", 100, required: false),
            Str("totpSecret", 128, required: false, pattern: "^[A-Z2-7]{16,}=*$"),
            new("isDataProvider", FieldType.Boolean),
            new("isActive", FieldType.Boolean),
        ],
        [
            new(FormCodes.DataProviderCredentials, ["isDataProvider", "isActive", "apiKey", "apiSecret"], "The active market-data provider needs an API key and secret."),
        ]),

        new FormRuleSet(GeminiConfig,
        [
            Str("modelName", 100, pattern: @"^[a-z0-9][a-z0-9.\-]*$"),
            new("useSearchGrounding", FieldType.Boolean),
            new("premarketScanTime", FieldType.Time),
            new("intradayPollSeconds", FieldType.Integer, Min: 10),
            new("volumeSpikeTriggerMultiplier", FieldType.Number, Min: 1, MinExclusive: true, Max: 9999.99m, Scale: 2),
            Score("patternWeightPct"),
            Score("newsWeightPct"),
            Score("oiGreeksWeightPct"),
            EnumOf<ImpactLevel>("vetoMinImpactLevel"),
            new("autoPauseOnExtremeMacro", FieldType.Boolean),
            new("maxCallsPerMinute", FieldType.Integer, Min: 1),
        ],
        [
            new(FormCodes.WeightsSum100, ["patternWeightPct", "newsWeightPct", "oiGreeksWeightPct"], "Pattern, news and OI/Greeks weights must add up to 100."),
            new(FormCodes.PremarketBeforeOpen, ["premarketScanTime"], "Pre-market scan must run before 09:15."),
        ]),

        new FormRuleSet(StrategyConfig,
        [
            EnumOf<StrikeSelection>("strikeSelection"),
            EnumOf<ExpiryPolicy>("expiryPolicy"),
            EnumOf<ProductType>("product"),
            EnumOf<Timeframe>("trendTimeframe"),
            EnumOf<Timeframe>("triggerTimeframe"),
            new("requireOptionConfirmation", FieldType.Boolean),
            Score("minCombinedConfidence"),
            new("entryStartTime", FieldType.Time),
            new("entryCutoffTime", FieldType.Time),
            new("squareOffTime", FieldType.Time),
            new("authLoginTime", FieldType.Time),
            new("authMaxRetries", FieldType.Integer, Min: 0, Max: 10),
        ],
        [
            new(FormCodes.SessionTimes, ["entryStartTime", "entryCutoffTime", "squareOffTime"], "Times must satisfy 09:15 ≤ entry start < entry cutoff < square-off ≤ 15:29."),
            new(FormCodes.AuthBeforeEntry, ["authLoginTime", "entryStartTime"], "Auto-login must run before the entry window opens."),
            new(FormCodes.TrendAboveTrigger, ["trendTimeframe", "triggerTimeframe"], "Trend timeframe must be higher than the trigger timeframe."),
        ]),

        new FormRuleSet(ChartPattern,
        [
            Str("patternCode", 60, pattern: "^[A-Z0-9_]+$"),
            Str("patternName", 120),
            EnumOf<PatternCategory>("category"),
            EnumOf<PatternDirection>("direction"),
            new("minCandlesRequired", FieldType.Integer, Min: 1),
            Score("defaultWeight"),
            new("requireVolumeConfirm", FieldType.Boolean),
            new("requireBreakoutClose", FieldType.Boolean),
            new("patternParameters", FieldType.Json),
            new("isActive", FieldType.Boolean),
        ],
        []),

        new FormRuleSet(ModeToggle,
        [
            EnumOf<TradeMode>("targetMode"),
            Str("accountClientId", 50),
            Str("confirmClientId", 50, required: false),
        ],
        [
            new(FormCodes.RealConfirmation, ["targetMode", "confirmClientId", "accountClientId"], "Type the account's client ID to confirm switching to REAL."),
        ]),

        new FormRuleSet(MarketHoliday,
        [
            new("holidayDate", FieldType.Date),
            EnumOf<Exchange>("exchange"),
            Str("description", 200),
            new("isSpecialSession", FieldType.Boolean),
            new("sessionOpenTime", FieldType.Time, Required: false),
            new("sessionCloseTime", FieldType.Time, Required: false),
        ],
        [
            new(FormCodes.SpecialSessionTimes, ["isSpecialSession", "sessionOpenTime", "sessionCloseTime"], "Special sessions need open < close; regular holidays must have no session times."),
        ]),
    }.ToDictionary(f => f.Form);

    private static FieldRule Money(string field, decimal min, bool minExclusive = false) =>
        new(field, FieldType.Number, Min: min, MinExclusive: minExclusive, Max: 9_999_999_999_999_999.99m, Scale: 2);

    private static FieldRule Pct(string field, decimal min, bool minExclusive = false, decimal max = 999.9999m, bool maxExclusive = false) =>
        new(field, FieldType.Number, Min: min, MinExclusive: minExclusive, Max: max, MaxExclusive: maxExclusive, Scale: 4);

    private static FieldRule Score(string field) => new(field, FieldType.Number, Min: 0, Max: 100, Scale: 2);

    private static FieldRule Str(string field, int maxLength, bool required = true, string? pattern = null) =>
        new(field, FieldType.String, Required: required, MaxLength: maxLength, Pattern: pattern);

    private static FieldRule EnumOf<TEnum>(string field) where TEnum : struct, Enum =>
        new(field, FieldType.Enum, AllowedValues: DbValue.AllValues(typeof(TEnum)).ToList());
}
