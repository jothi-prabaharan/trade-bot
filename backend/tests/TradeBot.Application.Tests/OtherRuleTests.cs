using System.Reflection;
using System.Text.Json;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

public sealed class MarketCalendarTests
{
    private static DateTime Ist(int y, int m, int d, int h, int min, int s = 0) => new DateTime(y, m, d, h, min, s, DateTimeKind.Utc) - FixedLimits.IstOffset;

    [Theory]
    [InlineData(9, 14, 59, RuleCodes.MarketClosed)]
    [InlineData(9, 15, 0, null)]
    [InlineData(15, 29, 59, null)]
    [InlineData(15, 30, 0, RuleCodes.MarketClosed)]
    public void Session_boundaries(int h, int m, int s, string? expected) =>
        Assert.Equal(expected, MarketCalendar.WeekendsOnly.CheckMarketOpen(Ist(2026, 10, 27, h, m, s))?.Code);

    [Fact]
    public void Special_session_uses_its_own_hours()
    {
        var cal = new MarketCalendar([new MarketHoliday { HolidayDate = new(2026, 11, 8), Description = "Special", IsSpecialSession = true, SessionOpenTime = new(18, 0), SessionCloseTime = new(19, 0) }]);
        Assert.Null(cal.CheckMarketOpen(Ist(2026, 11, 8, 18, 30)));
        Assert.Equal(RuleCodes.MarketClosed, cal.CheckMarketOpen(Ist(2026, 11, 8, 10, 0))?.Code);
    }
}

public sealed class ModeToggleTests
{
    private static TradingAccount Account() => new() { AccountName = "a", ClientId = "PM12345", UserName = "u", ApiKey = "k", ApiSecret = "s" };

    [Fact]
    public void Confirmed_switch_to_real_is_allowed() =>
        Assert.Empty(ModeToggleValidator.Validate(Account(), 0, TradeMode.Real, "PM12345"));

    [Fact]
    public void Blocked_while_trades_are_open() =>
        Assert.Contains(ModeToggleValidator.Validate(Account(), 1, TradeMode.Real, "PM12345"), v => v.Code == RuleCodes.ToggleBlockedOpenTrades);

    [Fact]
    public void Real_needs_credentials_and_confirmation()
    {
        var a = Account();
        a.ApiSecret = null;
        var codes = ModeToggleValidator.Validate(a, 0, TradeMode.Real, "wrong").Select(v => v.Code);
        Assert.Equal([RuleCodes.ApiCredentialsMissing, RuleCodes.RealConfirmationRequired], codes);
    }

    [Fact]
    public void Same_mode_is_rejected() =>
        Assert.Contains(ModeToggleValidator.Validate(Account(), 0, TradeMode.Paper, null), v => v.Code == RuleCodes.ModeUnchanged);
}

public sealed class OrderValidatorTests
{
    private static readonly SecurityMaster Security = new() { SecurityId = 1, PmlSymbol = "X", SecurityType = SecurityType.OptIdx, OptType = OptionType.Ce, StrikePrice = 25000, ExpiryDate = new(2026, 11, 3), LotSize = 65, TickSize = 0.05m };
    private static readonly Instrument Instrument = new() { Symbol = "NIFTY", LotSize = 65, FreezeQuantity = 1800, TickSize = 0.05m };

    private static Order Entry() => new()
    {
        OrderLegType = OrderLegType.Entry, TxnType = TxnType.Buy, Quantity = 65, Price = 100.5m, OrderLtp = 100, StopLossPrice = 85, TargetSellPrice = 135, OrderType = OrderType.Limit
    };

    private static Order StopLeg() => new()
    {
        OrderLegType = OrderLegType.StopLoss, ParentOrderId = 1, TxnType = TxnType.Sell, Quantity = 65, Price = 84, TriggerPrice = 85, OrderLtp = 100,
        StopLossPrice = 85, TargetSellPrice = 135, OrderType = OrderType.StopLossLimit
    };

    private static string[] Codes(Order o) => OrderValidator.Validate(o, Security, Instrument).Select(v => v.ToString()).ToArray();

    [Fact] public void Valid_entry() => Assert.Empty(Codes(Entry()));
    [Fact] public void Valid_stop_leg() => Assert.Empty(Codes(StopLeg()));

    [Fact]
    public void Entry_limit_above_target()
    {
        var o = Entry();
        o.Price = 136;
        Assert.Equal(["targetSellPrice:FX_TARGET_NOT_ABOVE_ENTRY"], Codes(o));
    }

    [Fact]
    public void Off_tick_and_non_lot_quantity()
    {
        var o = Entry();
        o.Price = 100.52m;
        o.Quantity = 70;
        Assert.Equal(["price:FX_PRICE_NOT_ON_TICK", "quantity:MS_QTY_NOT_LOT_MULTIPLE"], Codes(o).Order());
    }

    [Fact]
    public void Stop_leg_needs_trigger_and_sl_type()
    {
        var o = StopLeg();
        o.TriggerPrice = null;
        o.OrderType = OrderType.Limit;
        Assert.Equal(["orderType:FX_LEG_ORDER_TYPE_MISMATCH"], Codes(o));
        o.OrderType = OrderType.StopLossLimit;
        Assert.Equal(["triggerPrice:FX_SL_TRIGGER_INVALID"], Codes(o));
    }

    [Fact]
    public void Exit_leg_without_parent()
    {
        var o = StopLeg();
        o.ParentOrderId = null;
        Assert.Equal(["parentOrderId:FX_PARENT_LINK_INVALID"], Codes(o));
    }

    [Fact]
    public void Above_freeze_quantity()
    {
        var o = Entry();
        o.Quantity = 1820;
        Assert.Equal(["quantity:MS_QTY_ABOVE_FREEZE"], Codes(o));
    }

    [Fact]
    public void Trailing_sl_never_loosens() =>
        Assert.Equal([RuleCodes.TrailingSlLoosened], BracketRules.ValidateTrailingUpdate(95, 90, 135, 0.05m).Select(v => v.Code));
}

public sealed class RuleCatalogTests
{
    [Fact]
    public void Every_rule_code_is_catalogued()
    {
        var codes = typeof(RuleCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Contains(RuleCatalog.All, r => r.Code == c));
        Assert.All(RuleCatalog.All, r => Assert.StartsWith(r.Kind == RuleKind.Fixed ? "FX_" : "MS_", r.Code));
    }

    [Fact]
    public void Rules_document_serializes_with_db_vocabulary()
    {
        var json = JsonSerializer.Serialize(ValidationRulesDocument.Build(), ValidationRulesDocument.Json);
        Assert.Contains("\"form\":\"riskConfig\"", json);
        Assert.Contains("\"allowedValues\":[\"1m\",\"3m\",\"5m\",\"15m\",\"1h\",\"1d\"]", json);
        Assert.Contains("\"kind\":\"master\"", json);
    }
}
