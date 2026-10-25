using TradeBot.Application.Patterns;
using TradeBot.Application.Trading;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

public sealed class OptionBracketTests
{
    private static readonly BracketPolicy Defaults = BracketPolicy.From(new AccountRiskConfig());
    private static readonly DateTime Now = EntryContextBuilder.NowUtc;
    private static readonly DateOnly Expiry = EntryContextBuilder.TodayIst.AddDays(5);

    [Fact]
    public void Structural_bracket_is_capped_at_max_sl_pct()
    {
        var s = OptionBracket.MapStructural(100, 25000, 25000, OptionType.Ce, Expiry, Now, 24920, 25200)!;
        Assert.InRange((double)s.Greeks.Delta, 0.45, 0.6);
        var b = OptionBracket.Compute(100, s, Defaults, 0.05m);
        Assert.Equal(SlMethod.Structural, b.Method);
        Assert.Equal(100.50m, b.Entry);
        Assert.Equal(75.40m, b.StopLoss); // 25% cap below the 100.50 expected fill
        Assert.True(b.Target > 190 && b.RiskReward >= 2);
    }

    [Fact]
    public void Put_maps_underlying_rise_to_premium_fall()
    {
        var s = OptionBracket.MapStructural(100, 25000, 25000, OptionType.Pe, Expiry, Now, 25060, 24850)!;
        Assert.True(s.StopLoss < 100 && s.Target > 100 && s.Greeks.Delta < 0);
    }

    [Fact]
    public void Weak_structure_falls_back_to_fixed_pct_and_still_passes_validation()
    {
        var s = OptionBracket.MapStructural(100, 25000, 25000, OptionType.Ce, Expiry, Now, 24950, 25040);
        var b = OptionBracket.Compute(100, s, Defaults, 0.05m);
        Assert.Equal(SlMethod.FixedPctFallback, b.Method);
        Assert.Equal((100.50m, 85.45m, 130.65m), (b.Entry, b.StopLoss, b.Target));
        Assert.True(b.RiskReward >= 2);

        // The bracket must clear the validator's RR check at the slippage-adjusted limit price
        var d = EntryValidator.Validate(EntryContextBuilder.Valid() with { StopLossPrice = b.StopLoss, TargetPrice = b.Target });
        Assert.True(d.IsApproved, string.Join(",", d.Violations.Select(v => v.Code)));
    }

    [Fact]
    public void No_structure_when_iv_cannot_be_solved() =>
        Assert.Null(OptionBracket.MapStructural(10, 25300, 25000, OptionType.Ce, Expiry, Now, 25200, 25500)); // below intrinsic
}

public sealed class ContractSelectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    [Theory]
    [InlineData(ExpiryPolicy.NearestSkipExpiryDay, 2026, 11, 3)]
    [InlineData(ExpiryPolicy.Nearest, 2026, 10, 27)]
    [InlineData(ExpiryPolicy.MonthlyOnly, 2026, 10, 27)]
    public void Expiry_policies(ExpiryPolicy policy, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), ContractSelector.SelectExpiry([Today, new(2026, 11, 3), new(2026, 11, 24)], Today, policy));

    [Fact]
    public void Monthly_picks_last_expiry_of_the_month() =>
        Assert.Equal(new DateOnly(2026, 11, 24), ContractSelector.SelectExpiry([new(2026, 11, 3), new(2026, 11, 10), new(2026, 11, 24)], Today, ExpiryPolicy.MonthlyOnly));

    private static List<SecurityMaster> Chain() =>
        (from k in new[] { 24900m, 24950m, 25000m, 25050m, 25100m }
         from t in new[] { OptionType.Ce, OptionType.Pe }
         select new SecurityMaster { SecurityId = (long)k * 10 + (int)t, PmlSymbol = $"{k}{t}", SecurityType = SecurityType.OptIdx, StrikePrice = k, OptType = t, ExpiryDate = Today, LotSize = 65, TickSize = 0.05m }).ToList();

    [Theory]
    [InlineData(OptionType.Ce, StrikeSelection.Atm, 25000)]
    [InlineData(OptionType.Ce, StrikeSelection.Itm1, 24950)]
    [InlineData(OptionType.Pe, StrikeSelection.Itm1, 25050)]
    [InlineData(OptionType.Ce, StrikeSelection.Otm1, 25050)]
    [InlineData(OptionType.Pe, StrikeSelection.Otm1, 24950)]
    public void Strike_selection(OptionType type, StrikeSelection sel, decimal expected) =>
        Assert.Equal(expected, ContractSelector.SelectStrike(Chain(), 25012, type, sel)!.StrikePrice);
}

public sealed class SignalScoringTests
{
    [Fact]
    public void News_score_rewards_aligned_sentiment()
    {
        Assert.Equal(70, SignalScoring.NewsScore(PatternDirection.Bullish, 40));
        Assert.Equal(30, SignalScoring.NewsScore(PatternDirection.Bearish, 40));
        Assert.Equal(50, SignalScoring.NewsScore(PatternDirection.Bullish, null));
    }

    [Fact]
    public void Oi_score_and_combined_weights()
    {
        var oi = SignalScoring.OiGreeksScore(PatternDirection.Bullish, new OiSnapshot(1.3m, 1000, 5000), 0.5);
        Assert.Equal(77, oi);
        Assert.Equal(76.4m, SignalScoring.Combined(new GeminiNewsConfig { ModelName = "m" }, 80, 70, oi));
    }

    [Fact]
    public void Builder_produces_db_valid_signal()
    {
        var p = new ConfirmedPattern("CLS_DOUBLE_BOTTOM", PatternCategory.Classical, PatternDirection.Bullish, Timeframe.M5, 25010, 25015, 24920, 25200, 78,
            1.8m, MarketBias.Bullish, [], EntryContextBuilder.NowUtc, EntryContextBuilder.NowUtc);
        var option = new SecurityMaster { SecurityId = 500, InstrumentId = 10, PmlSymbol = "X", SecurityType = SecurityType.OptIdx, StrikePrice = 25000, OptType = OptionType.Ce, ExpiryDate = EntryContextBuilder.TodayIst.AddDays(5), LotSize = 65, TickSize = 0.05m };
        var built = SignalBuilder.Build(new SignalInputs(p, 7, new Instrument { InstrumentId = 10, Symbol = "NIFTY" }, option, 25015, 24990, 100, EntryContextBuilder.NowUtc,
            40, null, new OiSnapshot(1.2m, 100, 300), new GeminiNewsConfig { ModelName = "m" }, BracketPolicy.From(new AccountRiskConfig())));

        var s = built.Signal;
        Assert.True(s.MandatorySlPrice < s.EntryOptionLtp && s.EntryOptionLtp < s.MandatoryTargetPrice);
        Assert.Equal(SignalType.Buy, s.SignalType);
        Assert.InRange(s.CombinedAiConfidence, 0, 100);
        Assert.Contains("structuralPremiumSl", s.GreeksAndOi);

        var tighter = SignalBuilder.ForAccount(s, 100, BracketPolicy.From(new AccountRiskConfig { MaxSlPct = 20, SlippageBufferPct = 0.25m }), 0.05m);
        Assert.Equal(TickMath.RoundUp(100.25m * 0.8m, 0.05m), tighter.StopLoss);
    }
}

public sealed class OcoTests
{
    private static readonly DateTime T0 = EntryContextBuilder.NowUtc;

    private static BracketState Bracket(decimal entry = 100.25m, decimal sl = 85, decimal target = 130) =>
        BracketState.FromRisk(1, 1, 500, TradeMode.Paper, 65, entry, sl, target, 0.05m, new AccountRiskConfig(), new StrategyConfig());

    [Theory]
    [InlineData(114.9, 85)]
    [InlineData(115, 100)]
    [InlineData(118, 103)]
    [InlineData(127, 112)]
    [InlineData(129.99, 112)]
    public void Trailing_breakeven_then_steps(decimal peak, decimal expected) =>
        Assert.Equal(expected, TrailingStop.Next(100, 130, peak, 85, 50, 10, 0.05m));

    [Fact]
    public void Trailing_never_loosens() => Assert.Equal(110, TrailingStop.Next(100, 130, 116, 110, 50, 10, 0.05m));

    [Fact]
    public void Target_hit_exits_via_cancel_and_limit()
    {
        var ex = new PaperExchange(0.25m);
        var s = Bracket();
        var sl = ex.Place(500, TxnType.Sell, 65, OrderType.StopLossLimit, ExitPricing.SlLimitPrice(85, 2, 0.05m), 85, 0.05m, 100.25m, T0).Order;
        Assert.Equal(83.30m, sl.Price);

        Assert.Equal(OcoAction.None, OcoDecider.OnTick(s, 110, T0).Action);
        var d = OcoDecider.OnTick(s, 131, T0.AddSeconds(1));
        Assert.Equal((OcoAction.ExitAtTarget, 130m), (d.Action, d.Price!.Value));

        Assert.True(ex.Cancel(sl.Id));
        var (_, fill) = ex.Place(500, TxnType.Sell, 65, OrderType.Limit, 130, null, 0.05m, 131, T0.AddSeconds(1));
        Assert.Equal(130.65m, fill!.Price);
        Assert.Equal(TradeStatus.TargetHit, OcoDecider.OnExitFilled(s));
    }

    [Fact]
    public void Trailing_raise_then_exchange_stop_fill()
    {
        var ex = new PaperExchange(0.25m);
        var s = Bracket();
        var sl = ex.Place(500, TxnType.Sell, 65, OrderType.StopLossLimit, 83.30m, 85, 0.05m, 100.25m, T0).Order;

        var d = OcoDecider.OnTick(s, 116, T0);
        Assert.Equal((OcoAction.RaiseTrailing, 100.25m), (d.Action, d.Price!.Value));
        Assert.True(ex.Modify(sl.Id, ExitPricing.SlLimitPrice(100.25m, 2, 0.05m), 100.25m));

        Assert.Equal(OcoAction.StopTriggered, OcoDecider.OnTick(s, 99, T0.AddSeconds(1)).Action);
        var fill = Assert.Single(ex.OnTick(500, 99, T0.AddSeconds(1)));
        Assert.Equal(98.75m, fill.Price);
        Assert.Equal(TradeStatus.TrailingSlHit, OcoDecider.OnStopFilled(s));
    }

    [Fact]
    public void Gap_through_sl_limit_falls_back_to_aggressive_exit_after_2s()
    {
        var ex = new PaperExchange(0.25m);
        var s = Bracket();
        var sl = ex.Place(500, TxnType.Sell, 65, OrderType.StopLossLimit, 83.30m, 85, 0.05m, 100.25m, T0).Order;

        Assert.Empty(ex.OnTick(500, 80, T0));
        Assert.Equal(OcoAction.StopTriggered, OcoDecider.OnTick(s, 80, T0).Action);
        Assert.Equal(OcoAction.None, OcoDecider.OnTick(s, 79.5m, T0.AddSeconds(1)).Action);

        var d = OcoDecider.OnTick(s, 79, T0.AddSeconds(2));
        Assert.Equal((OcoAction.EmergencyExit, 77.40m, TradeStatus.SlHit), (d.Action, d.Price!.Value, d.Reason!.Value));
        Assert.True(ex.Cancel(sl.Id));
        var (_, fill) = ex.Place(500, TxnType.Sell, 65, OrderType.Limit, d.Price.Value, null, 0.05m, 79, T0.AddSeconds(2));
        Assert.Equal(78.80m, fill!.Price);
        Assert.Equal(TradeStatus.SlHit, OcoDecider.OnExitFilled(s));
    }

    [Fact]
    public void Forced_exit_reasons_and_closed_state()
    {
        var s = Bracket();
        var d = OcoDecider.ForceExit(s, TradeStatus.TimeSquareOff, 120);
        Assert.Equal((OcoAction.EmergencyExit, 117.60m), (d.Action, d.Price!.Value));
        Assert.Equal(OcoAction.None, OcoDecider.OnTick(s, 140, T0).Action);
        Assert.Equal(TradeStatus.TimeSquareOff, OcoDecider.OnExitFilled(s));
        Assert.Equal(OcoAction.None, OcoDecider.ForceExit(s, TradeStatus.ManualExit, 120).Action);
    }

    [Fact]
    public void Paper_entry_limit_fills_at_ltp_plus_slippage_but_never_above_limit()
    {
        var ex = new PaperExchange(0.25m);
        Assert.Equal(100.25m, ex.Place(500, TxnType.Buy, 65, OrderType.Limit, 100.50m, null, 0.05m, 100, T0).Fill!.Price);
        Assert.Null(ex.Place(500, TxnType.Buy, 65, OrderType.Limit, 100.50m, null, 0.05m, 100.6m, T0).Fill);
        Assert.Equal(100.50m, ex.Place(500, TxnType.Buy, 65, OrderType.Limit, 100.50m, null, 0.05m, 100.45m, T0).Fill!.Price);
    }

    [Theory]
    [InlineData(-3000, DayRiskAction.KillSwitch, 100)]
    [InlineData(-2999, DayRiskAction.None, 99.97)]
    [InlineData(6000, DayRiskAction.ProfitLock, 0)]
    public void Daily_risk_monitor(decimal net, DayRiskAction expected, decimal used)
    {
        var (action, pct) = DailyRiskMonitor.Evaluate(new DayRiskSnapshot(net, 0, 0), new AccountRiskConfig(), false, false);
        Assert.Equal((expected, used), (action, pct));
    }

    [Fact]
    public void Kill_switch_only_fires_once() =>
        Assert.Equal(DayRiskAction.None, DailyRiskMonitor.Evaluate(new DayRiskSnapshot(-5000, 0, 0), new AccountRiskConfig(), killed: true, locked: false).Action);
}
