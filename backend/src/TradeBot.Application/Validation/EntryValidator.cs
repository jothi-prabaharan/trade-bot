using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Validation;

public sealed record LiveQuote(decimal Ltp, DateTime TickTimeUtc);

/// <summary>News row resolved against the traded instrument (constituent weight already looked up).</summary>
public sealed record NewsImpact(
    long NewsId, DateOnly TradingDate, MarketBias Sentiment, ImpactLevel Impact,
    bool IsMacro, long? InstrumentId, long? CompanyInstrumentId, decimal ConstituentWeightPct);

/// <summary>Point-in-time snapshot of everything one account needs to validate one signal.</summary>
public sealed record EntryContext
{
    public required DateTime NowUtc { get; init; }
    public required MarketCalendar Calendar { get; init; }
    public required TradingAccount Account { get; init; }
    public required AccountRiskConfig Risk { get; init; }
    public AccountAuthToken? LatestToken { get; init; }
    public DailyAccountPnl? DayPnl { get; init; }
    public DailyBotState? BotState { get; init; }
    public required int OpenTradeCount { get; init; }
    public required bool HasOrderForSignal { get; init; }
    public int TradesToday { get; init; }
    /// <summary>Exit time of this account's latest SL / trailing-SL exit on the signal's instrument today.</summary>
    public DateTime? LastStopOutOnInstrumentUtc { get; init; }
    public required StrategyConfig Strategy { get; init; }
    public required GeminiNewsConfig Gemini { get; init; }
    public required Instrument Instrument { get; init; }
    public required SecurityMaster Security { get; init; }
    public required ChartPattern Pattern { get; init; }
    public required DetectedPattern Detection { get; init; }
    public required Signal Signal { get; init; }
    public DailyMarketSentiment? Sentiment { get; init; }
    public IReadOnlyList<NewsImpact> TodaysNews { get; init; } = [];
    public required LiveQuote Quote { get; init; }
    /// <summary>Account-specific bracket when its risk policy differs from the signal's reference policy.</summary>
    public decimal? StopLossPrice { get; init; }
    public decimal? TargetPrice { get; init; }
    /// <summary>REAL: mst.charge_rates brokerage; PAPER: simulated_brokerage_per_order.</summary>
    public required decimal BrokeragePerOrder { get; init; }
}

public sealed record SizingResult(
    int Lots, int Quantity, decimal LimitPrice, decimal StopLossPrice, decimal TargetPrice,
    decimal RiskAmount, decimal RiskRewardRatio, decimal RequiredMargin,
    int RiskSizedLots, string? DownsizedBy);

public sealed record EntryDecision(IReadOnlyList<RuleViolation> Violations, SizingResult? Sizing)
{
    public bool IsApproved => Violations.Count == 0 && Sizing is not null;
}

public static class EntryValidator
{
    public const string MaxLotsCap = "CAP_MAX_LOTS_PER_TRADE";

    public static EntryDecision Validate(EntryContext c)
    {
        var v = new List<RuleViolation>();
        var ist = FixedLimits.ToIst(c.NowUtc);
        var today = DateOnly.FromDateTime(ist);
        var istTime = TimeOnly.FromDateTime(ist);

        // Fixed
        if (c.Calendar.CheckMarketOpen(c.NowUtc) is { } closed) v.Add(closed);
        if (c.NowUtc - c.Quote.TickTimeUtc > FixedLimits.MaxTickAge) v.Add(RuleCatalog.Violation(RuleCodes.StaleTick));
        if (c.Security.Segment != Segment.Derivatives) v.Add(RuleCatalog.Violation(RuleCodes.InvalidSegment));
        if (c.Security.OptType is null || c.Security.SecurityType is not (SecurityType.OptIdx or SecurityType.OptStk))
            v.Add(RuleCatalog.Violation(RuleCodes.NotAnOption));
        if (c.Security.ExpiryDate < today) v.Add(RuleCatalog.Violation(RuleCodes.ContractExpired));
        if (c.Security.InstrumentId != c.Instrument.InstrumentId || c.Signal.SecurityId != c.Security.SecurityId)
            v.Add(RuleCatalog.Violation(RuleCodes.ContractMismatch));
        if (c.Signal.SignalType != SignalType.Buy) v.Add(RuleCatalog.Violation(RuleCodes.EntryMustBeBuy));
        v.AddRange(BracketRules.Validate(c.Signal.EntryOptionLtp, c.Signal.MandatorySlPrice, c.Signal.MandatoryTargetPrice));

        // Master: account
        var dayNet = c.DayPnl is null ? 0m : c.DayPnl.RealizedGrossPnl + c.DayPnl.UnrealizedMtmPnl - c.DayPnl.TotalCharges;
        if (!c.Account.IsActive) v.Add(RuleCatalog.Violation(RuleCodes.AccountInactive));
        if (c.BotState?.GlobalKillSwitchTriggered == true) v.Add(RuleCatalog.Violation(RuleCodes.GlobalKillSwitch));
        if (c.DayPnl?.KillSwitchTriggered == true) v.Add(RuleCatalog.Violation(RuleCodes.AccountKillSwitch));
        if (c.DayPnl?.ProfitLockTriggered == true) v.Add(RuleCatalog.Violation(RuleCodes.ProfitLocked));
        if (dayNet <= -c.Risk.MaxDailyLoss) v.Add(RuleCatalog.Violation(RuleCodes.DailyLossLimitReached));
        if (c.Account.TradeMode == TradeMode.Real)
        {
            if (string.IsNullOrEmpty(c.Account.ApiKey) || string.IsNullOrEmpty(c.Account.ApiSecret))
                v.Add(RuleCatalog.Violation(RuleCodes.ApiCredentialsMissing));
            if (c.LatestToken is not { LoginStatus: LoginStatus.Success, XJwtToken.Length: > 0, TokenExpiry: { } expiry }
                || expiry - c.NowUtc <= FixedLimits.TokenExpiryBuffer)
                v.Add(RuleCatalog.Violation(RuleCodes.TokenInvalid));
        }
        if (c.OpenTradeCount >= c.Risk.MaxOpenTrades) v.Add(RuleCatalog.Violation(RuleCodes.MaxOpenTrades));
        if (c.HasOrderForSignal) v.Add(RuleCatalog.Violation(RuleCodes.DuplicateSignal));
        if (c.TradesToday >= c.Risk.MaxTradesPerDay) v.Add(RuleCatalog.Violation(RuleCodes.MaxTradesPerDay));
        if (c.LastStopOutOnInstrumentUtc is { } stopOut && c.NowUtc - stopOut < TimeSpan.FromMinutes(c.Risk.ReentryCooldownMinutes))
            v.Add(RuleCatalog.Violation(RuleCodes.ReentryCooldown));

        // Master: instrument & strategy
        if (!c.Instrument.IsActive || !c.Instrument.IsTradable) v.Add(RuleCatalog.Violation(RuleCodes.InstrumentNotTradable));
        if (!c.Security.IsActive) v.Add(RuleCatalog.Violation(RuleCodes.SecurityInactive));
        if (c.Security.SyncedOn < today) v.Add(RuleCatalog.Violation(RuleCodes.SecurityMasterStale));
        if (c.Strategy.ExpiryPolicy == ExpiryPolicy.NearestSkipExpiryDay && c.Security.ExpiryDate == today)
            v.Add(RuleCatalog.Violation(RuleCodes.ExpiryDaySkipped));
        if (istTime < c.Strategy.EntryStartTime || istTime > c.Strategy.EntryCutoffTime) v.Add(RuleCatalog.Violation(RuleCodes.OutsideEntryWindow));
        if (c.Signal.CombinedAiConfidence < c.Strategy.MinCombinedConfidence) v.Add(RuleCatalog.Violation(RuleCodes.CombinedConfidenceLow));

        // Master: pattern & news
        if (!c.Pattern.IsActive) v.Add(RuleCatalog.Violation(RuleCodes.PatternInactive));
        if (!c.Detection.IsConfirmed) v.Add(RuleCatalog.Violation(RuleCodes.PatternNotConfirmed));
        if (c.Detection.ConfidenceScore < c.Risk.MinPatternConfidence) v.Add(RuleCatalog.Violation(RuleCodes.PatternConfidenceLow));
        CheckNews(c, today, v);

        // Master: price, RR, sizing
        var sizing = Size(c, dayNet, v);
        return new EntryDecision(v, v.Count == 0 ? sizing : null);
    }

    private static void CheckNews(EntryContext c, DateOnly today, List<RuleViolation> v)
    {
        var instrumentId = c.Instrument.InstrumentId;
        var relevant = c.TodaysNews.Where(n => n.TradingDate == today && (
            n.IsMacro || n.InstrumentId == instrumentId || n.CompanyInstrumentId == instrumentId
            || n.ConstituentWeightPct >= FixedLimits.NewsVetoMinConstituentWeightPct)).ToList();

        var halted = c.Sentiment?.TradingHaltActive == true
            || relevant.Any(n => n.Impact == ImpactLevel.ExtremeHalt && (!n.IsMacro || c.Gemini.AutoPauseOnExtremeMacro));
        if (halted) v.Add(RuleCatalog.Violation(RuleCodes.TradingHalt));

        if (!c.Risk.BlockTradeOnOpposingNews) return;
        var opposing = c.Security.OptType == OptionType.Ce ? MarketBias.Bearish : MarketBias.Bullish;
        if (relevant.Any(n => n.Sentiment == opposing && n.Impact >= c.Gemini.VetoMinImpactLevel))
            v.Add(RuleCatalog.Violation(RuleCodes.OpposingNewsVeto));
    }

    private static SizingResult? Size(EntryContext c, decimal dayNet, List<RuleViolation> v)
    {
        var r = c.Risk;
        var tick = c.Security.TickSize;
        var lotSize = c.Security.LotSize;
        var ltp = c.Quote.Ltp;

        if (ltp < r.MinSingleTradePrice || ltp > r.MaxSingleTradePrice) v.Add(RuleCatalog.Violation(RuleCodes.PremiumOutOfBand));

        var limit = TickMath.RoundUp(ltp * (1 + r.SlippageBufferPct / 100), tick);
        var sl = TickMath.RoundUp(c.StopLossPrice ?? c.Signal.MandatorySlPrice, tick);       // tighter
        var target = TickMath.RoundDown(c.TargetPrice ?? c.Signal.MandatoryTargetPrice, tick); // conservative

        var bracket = BracketRules.Validate(limit, sl, target);
        if (bracket.Count > 0)
        {
            v.AddRange(bracket);
            return null;
        }

        var unitRisk = limit - sl;
        var rr = Math.Round((target - limit) / unitRisk, 4);
        if (rr < r.MinRiskRewardRatio) v.Add(RuleCatalog.Violation(RuleCodes.RiskRewardBelowMin));
        if (unitRisk / limit * 100 > r.MaxSlPct) v.Add(RuleCatalog.Violation(RuleCodes.SlExceedsMaxPct));

        var lotRisk = unitRisk * lotSize;
        var riskBudget = r.AllocatedCapital * r.RiskPerTradePct / 100;
        var headroom = r.MaxDailyLoss - Math.Max(0, -dayNet);
        var reservedBrokerage = c.BrokeragePerOrder * FixedLimits.BrokerageOrdersPerTrade;

        var riskLots = (int)Math.Floor(Math.Floor(riskBudget / lotRisk) * r.LotMultiplier);
        // Ordered by reporting priority when several caps bind at once
        (string Code, int Lots)[] caps =
        [
            (RuleCodes.RiskBudgetTooSmall, riskLots),
            (RuleCodes.DailyLossHeadroomExhausted, headroom <= 0 ? 0 : (int)Math.Floor(headroom / lotRisk)),
            (RuleCodes.InsufficientMargin, (int)Math.Max(0, Math.Floor((r.AvailableMargin - reservedBrokerage) / (limit * lotSize)))),
            (RuleCodes.FreezeQuantityExceeded, c.Instrument.FreezeQuantity / lotSize),
            (MaxLotsCap, r.MaxLotsPerTrade),
        ];

        var binding = caps.MinBy(x => x.Lots);
        if (binding.Lots < 1)
        {
            v.Add(RuleCatalog.Violation(binding.Code));
            return null;
        }

        var qty = binding.Lots * lotSize;
        return new SizingResult(
            binding.Lots, qty, limit, sl, target,
            RiskAmount: unitRisk * qty, RiskRewardRatio: rr, RequiredMargin: limit * qty + reservedBrokerage,
            RiskSizedLots: riskLots, DownsizedBy: binding.Lots < riskLots ? binding.Code : null);
    }
}
