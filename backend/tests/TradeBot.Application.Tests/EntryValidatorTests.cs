using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;
using static TradeBot.Application.Tests.EntryContextBuilder;

namespace TradeBot.Application.Tests;

public sealed class EntryValidatorTests
{
    private static string[] Codes(EntryDecision d) => d.Violations.Select(v => v.Code).ToArray();

    private static void AssertRejected(string code, Action<Parts> mutate)
    {
        var decision = EntryValidator.Validate(Valid(mutate));
        Assert.False(decision.IsApproved);
        Assert.Contains(code, Codes(decision));
    }

    [Fact]
    public void Valid_paper_entry_is_sized_with_phase0_defaults()
    {
        var d = EntryValidator.Validate(Valid());

        Assert.True(d.IsApproved, string.Join(", ", Codes(d)));
        var s = d.Sizing!;
        Assert.Equal(100.50m, s.LimitPrice);       // LTP 100 + 0.5% slippage, on tick
        Assert.Equal(85m, s.StopLossPrice);
        Assert.Equal(135m, s.TargetPrice);
        Assert.Equal(1, s.Lots);                   // ₹2,000 risk / (15.50 × 65 = ₹1,007.50 per lot)
        Assert.Equal(65, s.Quantity);
        Assert.Equal(1007.50m, s.RiskAmount);
        Assert.Equal(2.2258m, s.RiskRewardRatio);
        Assert.Equal(6572.50m, s.RequiredMargin);  // 100.50 × 65 + 2 × ₹20
        Assert.Null(s.DownsizedBy);
    }

    [Fact]
    public void Valid_real_entry_passes_with_token_and_credentials() =>
        Assert.True(EntryValidator.Validate(Valid(p => p.Real())).IsApproved);

    // ---- Fixed rules ----
    [Fact] public void Before_open() => AssertRejected(RuleCodes.MarketClosed, p => p.NowUtc = new DateTime(2026, 10, 27, 3, 44, 59, DateTimeKind.Utc));
    [Fact] public void At_close() => AssertRejected(RuleCodes.MarketClosed, p => { p.NowUtc = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc); p.Quote = p.Quote with { TickTimeUtc = p.NowUtc }; });
    [Fact] public void Weekend() => AssertRejected(RuleCodes.NotTradingDay, p => p.NowUtc = p.NowUtc.AddDays(-2));
    [Fact] public void Holiday() => AssertRejected(RuleCodes.NotTradingDay, p => p.Holidays.Add(new MarketHoliday { HolidayDate = TodayIst, Description = "x" }));
    [Fact] public void Stale_tick() => AssertRejected(RuleCodes.StaleTick, p => p.Quote = p.Quote with { TickTimeUtc = p.NowUtc.AddSeconds(-4) });
    [Fact] public void Not_an_option() => AssertRejected(RuleCodes.NotAnOption, p => { p.Security.SecurityType = SecurityType.FutIdx; p.Security.OptType = null; });
    [Fact] public void Expired_contract() => AssertRejected(RuleCodes.ContractExpired, p => p.Security.ExpiryDate = TodayIst.AddDays(-1));
    [Fact] public void Contract_of_other_instrument() => AssertRejected(RuleCodes.ContractMismatch, p => p.Security.InstrumentId = 99);
    [Fact] public void Sell_entry() => AssertRejected(RuleCodes.EntryMustBeBuy, p => p.Signal.SignalType = SignalType.Sell);
    [Fact] public void Signal_sl_above_entry() => AssertRejected(RuleCodes.SlNotBelowEntry, p => p.Signal.MandatorySlPrice = 101);
    [Fact] public void Price_fell_through_stop_loss() => AssertRejected(RuleCodes.SlNotBelowEntry, p => p.Quote = p.Quote with { Ltp = 84 });

    // ---- Master: account ----
    [Fact] public void Inactive_account() => AssertRejected(RuleCodes.AccountInactive, p => p.Account.IsActive = false);
    [Fact] public void Global_kill_switch() => AssertRejected(RuleCodes.GlobalKillSwitch, p => p.BotState!.GlobalKillSwitchTriggered = true);
    [Fact] public void Account_kill_switch() => AssertRejected(RuleCodes.AccountKillSwitch, p => p.DayPnl!.KillSwitchTriggered = true);
    [Fact] public void Profit_locked() => AssertRejected(RuleCodes.ProfitLocked, p => p.DayPnl!.ProfitLockTriggered = true);
    [Fact] public void Daily_loss_reached_including_charges() => AssertRejected(RuleCodes.DailyLossLimitReached, p => { p.DayPnl!.RealizedGrossPnl = -2960; p.DayPnl.TotalCharges = 40; });
    [Fact] public void Real_without_credentials() => AssertRejected(RuleCodes.ApiCredentialsMissing, p => { p.Real(); p.Account.ApiSecret = null; });
    [Fact] public void Real_without_token() => AssertRejected(RuleCodes.TokenInvalid, p => { p.Real(); p.Token = null; });
    [Fact] public void Real_token_expiring_soon() => AssertRejected(RuleCodes.TokenInvalid, p => { p.Real(); p.Token!.TokenExpiry = p.NowUtc.AddMinutes(10); });
    [Fact] public void Real_failed_login() => AssertRejected(RuleCodes.TokenInvalid, p => { p.Real(); p.Token!.LoginStatus = LoginStatus.Failed; });
    [Fact] public void Max_open_trades() => AssertRejected(RuleCodes.MaxOpenTrades, p => p.OpenTradeCount = 1);
    [Fact] public void Duplicate_signal() => AssertRejected(RuleCodes.DuplicateSignal, p => p.HasOrderForSignal = true);

    [Fact]
    public void Paper_skips_broker_only_rules()
    {
        var d = EntryValidator.Validate(Valid(p => { p.Account.ApiKey = null; p.Token = null; }));
        Assert.True(d.IsApproved);
    }

    // ---- Master: instrument & strategy ----
    [Fact] public void Instrument_not_tradable() => AssertRejected(RuleCodes.InstrumentNotTradable, p => p.Instrument.IsTradable = false);
    [Fact] public void Security_inactive() => AssertRejected(RuleCodes.SecurityInactive, p => p.Security.IsActive = false);
    [Fact] public void Scrip_master_stale() => AssertRejected(RuleCodes.SecurityMasterStale, p => p.Security.SyncedOn = TodayIst.AddDays(-1));
    [Fact] public void Expiry_day_skipped() => AssertRejected(RuleCodes.ExpiryDaySkipped, p => p.Security.ExpiryDate = TodayIst);
    [Fact] public void After_entry_cutoff() => AssertRejected(RuleCodes.OutsideEntryWindow, p => { p.NowUtc = new DateTime(2026, 10, 27, 9, 16, 0, DateTimeKind.Utc); p.Quote = p.Quote with { TickTimeUtc = p.NowUtc }; });
    [Fact] public void Low_combined_confidence() => AssertRejected(RuleCodes.CombinedConfidenceLow, p => p.Signal.CombinedAiConfidence = 69.99m);

    [Fact]
    public void Expiry_day_allowed_when_policy_is_nearest() =>
        Assert.True(EntryValidator.Validate(Valid(p => { p.Security.ExpiryDate = TodayIst; p.Strategy.ExpiryPolicy = ExpiryPolicy.Nearest; })).IsApproved);

    // ---- Master: pattern & news ----
    [Fact] public void Pattern_inactive() => AssertRejected(RuleCodes.PatternInactive, p => p.Pattern.IsActive = false);
    [Fact] public void Pattern_unconfirmed() => AssertRejected(RuleCodes.PatternNotConfirmed, p => p.Detection.IsConfirmed = false);
    [Fact] public void Pattern_confidence_low() => AssertRejected(RuleCodes.PatternConfidenceLow, p => p.Detection.ConfidenceScore = 69);
    [Fact] public void Sentiment_halt() => AssertRejected(RuleCodes.TradingHalt, p => p.Sentiment!.TradingHaltActive = true);

    private static NewsImpact News(MarketBias bias, ImpactLevel impact, bool macro = false, long? instrumentId = null, decimal weight = 0, int dayOffset = 0) =>
        new(1, TodayIst.AddDays(dayOffset), bias, impact, macro, instrumentId, null, weight);

    [Fact] public void Bearish_high_news_vetoes_call() => AssertRejected(RuleCodes.OpposingNewsVeto, p => p.News.Add(News(MarketBias.Bearish, ImpactLevel.High, instrumentId: 10)));
    [Fact] public void Bullish_high_news_vetoes_put() => AssertRejected(RuleCodes.OpposingNewsVeto, p => { p.Security.OptType = OptionType.Pe; p.News.Add(News(MarketBias.Bullish, ImpactLevel.High, macro: true)); });
    [Fact] public void Heavy_constituent_news_vetoes() => AssertRejected(RuleCodes.OpposingNewsVeto, p => p.News.Add(News(MarketBias.Bearish, ImpactLevel.High, weight: 5)));
    [Fact] public void Extreme_constituent_news_halts() => AssertRejected(RuleCodes.TradingHalt, p => p.News.Add(News(MarketBias.Bullish, ImpactLevel.ExtremeHalt, weight: 12)));

    [Theory]
    [InlineData(MarketBias.Bearish, ImpactLevel.Medium, false, 0, 0)]   // below veto level
    [InlineData(MarketBias.Bullish, ImpactLevel.High, true, 0, 0)]      // same direction
    [InlineData(MarketBias.Bearish, ImpactLevel.High, false, 4.99, 0)]  // light constituent
    [InlineData(MarketBias.Bearish, ImpactLevel.High, true, 0, -1)]     // yesterday
    public void News_that_does_not_veto(MarketBias bias, ImpactLevel impact, bool macro, double weight, int dayOffset) =>
        Assert.True(EntryValidator.Validate(Valid(p => p.News.Add(News(bias, impact, macro, weight: (decimal)weight, dayOffset: dayOffset)))).IsApproved);

    [Fact]
    public void Veto_disabled_by_account_flag() =>
        Assert.True(EntryValidator.Validate(Valid(p => { p.Risk.BlockTradeOnOpposingNews = false; p.News.Add(News(MarketBias.Bearish, ImpactLevel.High, macro: true)); })).IsApproved);

    [Fact]
    public void Extreme_macro_ignored_when_auto_pause_off_but_still_vetoes_opposite_side()
    {
        var sameSide = EntryValidator.Validate(Valid(p => { p.Gemini.AutoPauseOnExtremeMacro = false; p.News.Add(News(MarketBias.Bullish, ImpactLevel.ExtremeHalt, macro: true)); }));
        Assert.True(sameSide.IsApproved);
        AssertRejected(RuleCodes.OpposingNewsVeto, p => { p.Gemini.AutoPauseOnExtremeMacro = false; p.News.Add(News(MarketBias.Bearish, ImpactLevel.ExtremeHalt, macro: true)); });
    }

    // ---- Master: price, RR & sizing ----
    [Fact] public void Premium_below_band() => AssertRejected(RuleCodes.PremiumOutOfBand, p => { p.Quote = p.Quote with { Ltp = 29.95m }; p.Signal.MandatorySlPrice = 25; p.Signal.MandatoryTargetPrice = 45; });
    [Fact] public void Premium_above_band() => AssertRejected(RuleCodes.PremiumOutOfBand, p => { p.Quote = p.Quote with { Ltp = 400.05m }; p.Signal.MandatorySlPrice = 340; p.Signal.MandatoryTargetPrice = 540; });
    [Fact] public void Slippage_breaks_min_rr() => AssertRejected(RuleCodes.RiskRewardBelowMin, p => p.Signal.MandatoryTargetPrice = 130); // 29.5 / 15.5 = 1.90
    [Fact] public void Sl_too_wide() => AssertRejected(RuleCodes.SlExceedsMaxPct, p => { p.Signal.MandatorySlPrice = 70; p.Signal.MandatoryTargetPrice = 170; });
    [Fact] public void Risk_budget_too_small() => AssertRejected(RuleCodes.RiskBudgetTooSmall, p => { p.Quote = p.Quote with { Ltp = 300 }; p.Signal.MandatorySlPrice = 250; p.Signal.MandatoryTargetPrice = 420; });
    [Fact] public void Headroom_exhausted() => AssertRejected(RuleCodes.DailyLossHeadroomExhausted, p => p.DayPnl!.RealizedGrossPnl = -2500);
    [Fact] public void No_margin() => AssertRejected(RuleCodes.InsufficientMargin, p => p.Risk.AvailableMargin = 6500);
    [Fact] public void Lot_above_freeze() => AssertRejected(RuleCodes.FreezeQuantityExceeded, p => p.Instrument.FreezeQuantity = 60);

    [Fact]
    public void Downsized_by_daily_loss_headroom()
    {
        var s = EntryValidator.Validate(Valid(p => p.Risk.RiskPerTradePct = 5)).Sizing!; // 4 lots by risk, 2 by ₹3,000 headroom
        Assert.Equal((4, 2, RuleCodes.DailyLossHeadroomExhausted), (s.RiskSizedLots, s.Lots, s.DownsizedBy));
    }

    [Fact]
    public void Downsized_by_margin()
    {
        var s = EntryValidator.Validate(Valid(p => { p.Risk.RiskPerTradePct = 5; p.Risk.AvailableMargin = 10000; })).Sizing!;
        Assert.Equal((1, RuleCodes.InsufficientMargin), (s.Lots, s.DownsizedBy));
    }

    [Fact]
    public void Downsized_by_max_lots()
    {
        var s = EntryValidator.Validate(Valid(p => { p.Risk.RiskPerTradePct = 10; p.Risk.MaxDailyLoss = 20000; p.Risk.MaxLotsPerTrade = 3; })).Sizing!;
        Assert.Equal((9, 3, EntryValidator.MaxLotsCap), (s.RiskSizedLots, s.Lots, s.DownsizedBy));
    }

    [Fact]
    public void Lot_multiplier_scales_risk_lots()
    {
        var s = EntryValidator.Validate(Valid(p => { p.Risk.RiskPerTradePct = 5; p.Risk.MaxDailyLoss = 20000; p.Risk.LotMultiplier = 0.5m; })).Sizing!;
        Assert.Equal((2, 2), (s.RiskSizedLots, s.Lots));
    }

    [Fact]
    public void Profit_does_not_extend_risk_headroom()
    {
        var s = EntryValidator.Validate(Valid(p => { p.Risk.RiskPerTradePct = 5; p.DayPnl!.RealizedGrossPnl = 5000; })).Sizing!;
        Assert.Equal(2, s.Lots);
    }

    [Fact]
    public void Prices_are_rounded_to_tick_conservatively()
    {
        var s = EntryValidator.Validate(Valid(p => { p.Quote = p.Quote with { Ltp = 100.02m }; p.Signal.MandatorySlPrice = 84.98m; p.Signal.MandatoryTargetPrice = 135.04m; })).Sizing!;
        Assert.Equal((100.55m, 85.00m, 135.00m), (s.LimitPrice, s.StopLossPrice, s.TargetPrice));
    }

    [Fact]
    public void All_failures_are_reported_together()
    {
        var d = EntryValidator.Validate(Valid(p => { p.Account.IsActive = false; p.Pattern.IsActive = false; p.OpenTradeCount = 5; }));
        Assert.Superset(new HashSet<string> { RuleCodes.AccountInactive, RuleCodes.PatternInactive, RuleCodes.MaxOpenTrades }, Codes(d).ToHashSet());
        Assert.Null(d.Sizing);
    }
}

public sealed class TradePacingTests
{
    [Fact]
    public void Daily_trade_cap()
    {
        var d = EntryValidator.Validate(EntryContextBuilder.Valid() with { TradesToday = 3 });
        Assert.Contains(d.Violations, v => v.Code == RuleCodes.MaxTradesPerDay);
    }

    [Fact]
    public void Reentry_cooldown_blocks_for_15_minutes()
    {
        var blocked = EntryValidator.Validate(EntryContextBuilder.Valid() with { LastStopOutOnInstrumentUtc = EntryContextBuilder.NowUtc.AddMinutes(-14) });
        Assert.Contains(blocked.Violations, v => v.Code == RuleCodes.ReentryCooldown);
        var allowed = EntryValidator.Validate(EntryContextBuilder.Valid() with { LastStopOutOnInstrumentUtc = EntryContextBuilder.NowUtc.AddMinutes(-15) });
        Assert.True(allowed.IsApproved);
    }

    [Fact]
    public void Account_bracket_override_is_used_for_sizing()
    {
        var s = EntryValidator.Validate(EntryContextBuilder.Valid() with { StopLossPrice = 90, TargetPrice = 125 }).Sizing!;
        Assert.Equal((90m, 125m), (s.StopLossPrice, s.TargetPrice));
    }
}
