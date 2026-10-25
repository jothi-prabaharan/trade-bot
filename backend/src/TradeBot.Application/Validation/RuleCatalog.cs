namespace TradeBot.Application.Validation;

public enum RuleKind { Fixed, Master }

public enum RuleScope { Signal, Entry, Order, Sizing, Account, ModeToggle, Form }

public sealed record RuleInfo(string Code, RuleKind Kind, RuleScope Scope, string Message, string Source);

public sealed record RuleViolation(string Code, string Message, string? Field = null)
{
    public override string ToString() => Field is null ? Code : $"{Field}:{Code}";
}

/// <summary>Single source of truth for every validation code; served to the UI and rendered in docs.</summary>
public static class RuleCatalog
{
    private static readonly Dictionary<string, RuleInfo> Rules = new[]
    {
        // ---- Fixed: market & instrument ----
        R(RuleCodes.MarketClosed, RuleKind.Fixed, RuleScope.Entry, "Outside market hours (09:15:00–15:29:59 IST).", "FixedLimits"),
        R(RuleCodes.NotTradingDay, RuleKind.Fixed, RuleScope.Entry, "Not a trading day (weekend or NSE holiday).", "FixedLimits + mst.market_holidays"),
        R(RuleCodes.StaleTick, RuleKind.Fixed, RuleScope.Entry, "Option LTP is older than 3 seconds.", "FixedLimits"),
        R(RuleCodes.InvalidSegment, RuleKind.Fixed, RuleScope.Order, "Segment must be D (derivatives).", "FixedLimits"),
        R(RuleCodes.NotAnOption, RuleKind.Fixed, RuleScope.Entry, "Contract must be an option (CE/PE).", "FixedLimits"),
        R(RuleCodes.InvalidTradeMode, RuleKind.Fixed, RuleScope.Account, "Trade mode must be PAPER or REAL.", "FixedLimits"),
        R(RuleCodes.ContractExpired, RuleKind.Fixed, RuleScope.Entry, "Option contract has expired.", "FixedLimits"),
        R(RuleCodes.ContractMismatch, RuleKind.Fixed, RuleScope.Entry, "Option contract does not belong to the signal's instrument.", "FixedLimits"),
        R(RuleCodes.EntryMustBeBuy, RuleKind.Fixed, RuleScope.Signal, "Only option buying is allowed (entry must be BUY).", "FixedLimits"),

        // ---- Fixed: mandatory SL / target ----
        R(RuleCodes.SlRequired, RuleKind.Fixed, RuleScope.Signal, "Stop-loss price is mandatory.", "FixedLimits"),
        R(RuleCodes.TargetRequired, RuleKind.Fixed, RuleScope.Signal, "Target sell price is mandatory.", "FixedLimits"),
        R(RuleCodes.SlNotPositive, RuleKind.Fixed, RuleScope.Signal, "Stop-loss price must be greater than 0.", "FixedLimits"),
        R(RuleCodes.SlNotBelowEntry, RuleKind.Fixed, RuleScope.Signal, "Stop-loss must be below the entry price.", "FixedLimits"),
        R(RuleCodes.TargetNotAboveEntry, RuleKind.Fixed, RuleScope.Signal, "Target must be above the entry price.", "FixedLimits"),
        R(RuleCodes.PriceNotOnTick, RuleKind.Fixed, RuleScope.Order, "Price is not a multiple of the tick size.", "mst.security_master.tick_size"),
        R(RuleCodes.LegOrderTypeMismatch, RuleKind.Fixed, RuleScope.Order, "STOP_LOSS legs must be SL orders and TARGET_SELL legs LMT orders.", "FixedLimits"),
        R(RuleCodes.SlTriggerInvalid, RuleKind.Fixed, RuleScope.Order, "SL orders need trigger > 0 and sell limit price <= trigger.", "FixedLimits"),
        R(RuleCodes.ParentLinkInvalid, RuleKind.Fixed, RuleScope.Order, "Exit legs must reference their entry order; entries must not.", "FixedLimits"),
        R(RuleCodes.TrailingSlLoosened, RuleKind.Fixed, RuleScope.Order, "Trailing stop-loss can only move up.", "FixedLimits"),
        R(RuleCodes.TrailingSlAtTarget, RuleKind.Fixed, RuleScope.Order, "Trailing stop-loss must stay below the target.", "FixedLimits"),

        // ---- Fixed: mode toggle ----
        R(RuleCodes.ModeUnchanged, RuleKind.Fixed, RuleScope.ModeToggle, "Account is already in the requested mode.", "FixedLimits"),
        R(RuleCodes.RealConfirmationRequired, RuleKind.Fixed, RuleScope.ModeToggle, "Type the account's client ID to confirm switching to REAL.", "FixedLimits"),

        // ---- Master: account state ----
        R(RuleCodes.AccountInactive, RuleKind.Master, RuleScope.Account, "Account is inactive.", "mst.trading_accounts.is_active"),
        R(RuleCodes.GlobalKillSwitch, RuleKind.Master, RuleScope.Account, "Global kill switch is active for today.", "trn.daily_bot_state"),
        R(RuleCodes.AccountKillSwitch, RuleKind.Master, RuleScope.Account, "Account kill switch is active for today.", "trn.daily_account_pnl.kill_switch_triggered"),
        R(RuleCodes.ProfitLocked, RuleKind.Master, RuleScope.Account, "Daily profit target reached; no new entries.", "trn.daily_account_pnl.profit_lock_triggered"),
        R(RuleCodes.DailyLossLimitReached, RuleKind.Master, RuleScope.Account, "Daily loss limit reached.", "mst.account_risk_config.max_daily_loss"),
        R(RuleCodes.ApiCredentialsMissing, RuleKind.Master, RuleScope.Account, "REAL mode needs API key and secret.", "mst.trading_accounts"),
        R(RuleCodes.TokenInvalid, RuleKind.Master, RuleScope.Account, "No valid Paytm Money token (expires within 15 min or login failed).", "mst.account_auth_tokens"),
        R(RuleCodes.MaxOpenTrades, RuleKind.Master, RuleScope.Account, "Maximum concurrent open trades reached.", "mst.account_risk_config.max_open_trades"),
        R(RuleCodes.DuplicateSignal, RuleKind.Master, RuleScope.Account, "An order already exists for this signal on this account.", "trn.orders"),
        R(RuleCodes.MaxTradesPerDay, RuleKind.Master, RuleScope.Account, "Daily trade count limit reached.", "mst.account_risk_config.max_trades_per_day"),
        R(RuleCodes.ReentryCooldown, RuleKind.Master, RuleScope.Account, "Re-entry cooldown after a stop-loss exit on this instrument.", "mst.account_risk_config.reentry_cooldown_minutes"),
        R(RuleCodes.ToggleBlockedOpenTrades, RuleKind.Master, RuleScope.ModeToggle, "Mode can only change while the account has no open trades.", "trn.daily_trades"),

        // ---- Master: instrument / strategy ----
        R(RuleCodes.InstrumentNotTradable, RuleKind.Master, RuleScope.Entry, "Instrument is not enabled for trading.", "mst.instruments.is_tradable"),
        R(RuleCodes.SecurityInactive, RuleKind.Master, RuleScope.Entry, "Option contract is inactive.", "mst.security_master.is_active"),
        R(RuleCodes.SecurityMasterStale, RuleKind.Master, RuleScope.Entry, "Scrip master was not synced today.", "mst.security_master.synced_on"),
        R(RuleCodes.ExpiryDaySkipped, RuleKind.Master, RuleScope.Entry, "Expiry-day contracts are skipped by the expiry policy.", "mst.strategy_config.expiry_policy"),
        R(RuleCodes.OutsideEntryWindow, RuleKind.Master, RuleScope.Entry, "Outside the strategy entry window.", "mst.strategy_config.entry_start_time / entry_cutoff_time"),
        R(RuleCodes.CombinedConfidenceLow, RuleKind.Master, RuleScope.Signal, "Combined AI confidence below the strategy minimum.", "mst.strategy_config.min_combined_confidence"),

        // ---- Master: pattern & news ----
        R(RuleCodes.PatternInactive, RuleKind.Master, RuleScope.Signal, "Chart pattern is disabled.", "mst.chart_patterns.is_active"),
        R(RuleCodes.PatternNotConfirmed, RuleKind.Master, RuleScope.Signal, "Pattern breakout is not confirmed.", "trn.detected_patterns.is_confirmed"),
        R(RuleCodes.PatternConfidenceLow, RuleKind.Master, RuleScope.Signal, "Pattern confidence below the account minimum.", "mst.account_risk_config.min_pattern_confidence"),
        R(RuleCodes.TradingHalt, RuleKind.Master, RuleScope.Entry, "Trading halted on this instrument (EXTREME news).", "trn.daily_market_sentiment / trn.company_daily_news"),
        R(RuleCodes.OpposingNewsVeto, RuleKind.Master, RuleScope.Entry, "Opposing HIGH/EXTREME news today vetoes this direction.", "mst.account_risk_config.block_trade_on_opposing_news + mst.gemini_news_config.veto_min_impact_level"),

        // ---- Master: price, RR and sizing ----
        R(RuleCodes.PremiumOutOfBand, RuleKind.Master, RuleScope.Sizing, "Option premium outside the allowed band.", "mst.account_risk_config.min/max_single_trade_price"),
        R(RuleCodes.RiskRewardBelowMin, RuleKind.Master, RuleScope.Sizing, "Risk-to-reward below the account minimum.", "mst.account_risk_config.min_risk_reward_ratio"),
        R(RuleCodes.SlExceedsMaxPct, RuleKind.Master, RuleScope.Sizing, "Stop-loss distance exceeds max SL %.", "mst.account_risk_config.max_sl_pct"),
        R(RuleCodes.RiskBudgetTooSmall, RuleKind.Master, RuleScope.Sizing, "Risk per trade cannot cover even 1 lot.", "mst.account_risk_config.risk_per_trade_pct"),
        R(RuleCodes.DailyLossHeadroomExhausted, RuleKind.Master, RuleScope.Sizing, "Remaining daily-loss headroom cannot cover 1 lot.", "mst.account_risk_config.max_daily_loss"),
        R(RuleCodes.InsufficientMargin, RuleKind.Master, RuleScope.Sizing, "Available margin cannot cover 1 lot plus charges.", "mst.account_risk_config.available_margin"),
        R(RuleCodes.FreezeQuantityExceeded, RuleKind.Master, RuleScope.Sizing, "1 lot exceeds the exchange freeze quantity.", "mst.instruments.freeze_quantity"),
        R(RuleCodes.QtyNotLotMultiple, RuleKind.Master, RuleScope.Order, "Quantity must be a multiple of the lot size.", "mst.security_master.lot_size"),
        R(RuleCodes.QtyAboveFreeze, RuleKind.Master, RuleScope.Order, "Quantity exceeds the exchange freeze quantity.", "mst.instruments.freeze_quantity"),
    }.ToDictionary(r => r.Code);

    public static IReadOnlyCollection<RuleInfo> All => Rules.Values;

    public static RuleViolation Violation(string code, string? field = null) => new(code, Rules[code].Message, field);

    private static RuleInfo R(string code, RuleKind kind, RuleScope scope, string message, string source) => new(code, kind, scope, message, source);
}

public static class RuleCodes
{
    public const string MarketClosed = "FX_MARKET_CLOSED";
    public const string NotTradingDay = "FX_NOT_TRADING_DAY";
    public const string StaleTick = "FX_STALE_TICK";
    public const string InvalidSegment = "FX_INVALID_SEGMENT";
    public const string NotAnOption = "FX_NOT_AN_OPTION";
    public const string InvalidTradeMode = "FX_INVALID_TRADE_MODE";
    public const string ContractExpired = "FX_CONTRACT_EXPIRED";
    public const string ContractMismatch = "FX_CONTRACT_MISMATCH";
    public const string EntryMustBeBuy = "FX_ENTRY_MUST_BE_BUY";
    public const string SlRequired = "FX_SL_REQUIRED";
    public const string TargetRequired = "FX_TARGET_REQUIRED";
    public const string SlNotPositive = "FX_SL_NOT_POSITIVE";
    public const string SlNotBelowEntry = "FX_SL_NOT_BELOW_ENTRY";
    public const string TargetNotAboveEntry = "FX_TARGET_NOT_ABOVE_ENTRY";
    public const string PriceNotOnTick = "FX_PRICE_NOT_ON_TICK";
    public const string LegOrderTypeMismatch = "FX_LEG_ORDER_TYPE_MISMATCH";
    public const string SlTriggerInvalid = "FX_SL_TRIGGER_INVALID";
    public const string ParentLinkInvalid = "FX_PARENT_LINK_INVALID";
    public const string TrailingSlLoosened = "FX_TRAILING_SL_LOOSENED";
    public const string TrailingSlAtTarget = "FX_TRAILING_SL_AT_TARGET";
    public const string ModeUnchanged = "FX_MODE_UNCHANGED";
    public const string RealConfirmationRequired = "FX_REAL_CONFIRMATION_REQUIRED";

    public const string AccountInactive = "MS_ACCOUNT_INACTIVE";
    public const string GlobalKillSwitch = "MS_GLOBAL_KILL_SWITCH";
    public const string AccountKillSwitch = "MS_ACCOUNT_KILL_SWITCH";
    public const string ProfitLocked = "MS_PROFIT_LOCKED";
    public const string DailyLossLimitReached = "MS_DAILY_LOSS_LIMIT_REACHED";
    public const string ApiCredentialsMissing = "MS_API_CREDENTIALS_MISSING";
    public const string TokenInvalid = "MS_TOKEN_INVALID";
    public const string MaxOpenTrades = "MS_MAX_OPEN_TRADES";
    public const string DuplicateSignal = "MS_DUPLICATE_SIGNAL";
    public const string MaxTradesPerDay = "MS_MAX_TRADES_PER_DAY";
    public const string ReentryCooldown = "MS_REENTRY_COOLDOWN";
    public const string ToggleBlockedOpenTrades = "MS_TOGGLE_BLOCKED_OPEN_TRADES";
    public const string InstrumentNotTradable = "MS_INSTRUMENT_NOT_TRADABLE";
    public const string SecurityInactive = "MS_SECURITY_INACTIVE";
    public const string SecurityMasterStale = "MS_SECURITY_MASTER_STALE";
    public const string ExpiryDaySkipped = "MS_EXPIRY_DAY_SKIPPED";
    public const string OutsideEntryWindow = "MS_OUTSIDE_ENTRY_WINDOW";
    public const string CombinedConfidenceLow = "MS_COMBINED_CONFIDENCE_LOW";
    public const string PatternInactive = "MS_PATTERN_INACTIVE";
    public const string PatternNotConfirmed = "MS_PATTERN_NOT_CONFIRMED";
    public const string PatternConfidenceLow = "MS_PATTERN_CONFIDENCE_LOW";
    public const string TradingHalt = "MS_TRADING_HALT";
    public const string OpposingNewsVeto = "MS_OPPOSING_NEWS_VETO";
    public const string PremiumOutOfBand = "MS_PREMIUM_OUT_OF_BAND";
    public const string RiskRewardBelowMin = "MS_RR_BELOW_MIN";
    public const string SlExceedsMaxPct = "MS_SL_EXCEEDS_MAX_PCT";
    public const string RiskBudgetTooSmall = "MS_RISK_BUDGET_TOO_SMALL";
    public const string DailyLossHeadroomExhausted = "MS_DAILY_LOSS_HEADROOM_EXHAUSTED";
    public const string InsufficientMargin = "MS_INSUFFICIENT_MARGIN";
    public const string FreezeQuantityExceeded = "MS_FREEZE_QTY_EXCEEDED";
    public const string QtyNotLotMultiple = "MS_QTY_NOT_LOT_MULTIPLE";
    public const string QtyAboveFreeze = "MS_QTY_ABOVE_FREEZE";
}
