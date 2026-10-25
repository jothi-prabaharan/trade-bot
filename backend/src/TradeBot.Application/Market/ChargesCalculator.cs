using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Market;

public sealed record ChargeBreakdown(decimal Brokerage, decimal Stt, decimal ExchangeTxn, decimal SebiFee, decimal StampDuty, decimal Gst)
{
    public decimal Total => Brokerage + Stt + ExchangeTxn + SebiFee + StampDuty + Gst;

    public static ChargeBreakdown operator +(ChargeBreakdown a, ChargeBreakdown b) =>
        new(a.Brokerage + b.Brokerage, a.Stt + b.Stt, a.ExchangeTxn + b.ExchangeTxn, a.SebiFee + b.SebiFee, a.StampDuty + b.StampDuty, a.Gst + b.Gst);
}

/// <summary>Statutory + brokerage charges per executed order (options: STT on sell premium, stamp on buy).</summary>
public static class ChargesCalculator
{
    /// <summary>Fallback when mst.charge_rates has no row (NSE options, Oct-2024 schedule).</summary>
    public static readonly ChargeRate DefaultOptionRates = new()
    {
        SecurityType = SecurityType.OptIdx, BrokeragePerOrder = 20, SttSellPct = 0.1m, ExchangeTxnPct = 0.03503m,
        SebiFeePct = 0.0001m, StampDutyBuyPct = 0.003m, GstPct = 18m,
    };

    public static ChargeBreakdown ForOrder(ChargeRate rate, TxnType side, decimal price, int quantity, decimal? brokerageOverride = null)
    {
        var turnover = price * quantity;
        var brokerage = brokerageOverride ?? rate.BrokeragePerOrder;
        var stt = side == TxnType.Sell ? Round(turnover * rate.SttSellPct / 100) : 0;
        var exchange = Round(turnover * rate.ExchangeTxnPct / 100);
        var sebi = Round(turnover * rate.SebiFeePct / 100);
        var stamp = side == TxnType.Buy ? Round(turnover * rate.StampDutyBuyPct / 100) : 0;
        var gst = Round((brokerage + exchange + sebi) * rate.GstPct / 100);
        return new ChargeBreakdown(brokerage, stt, exchange, sebi, stamp, gst);
    }

    /// <summary>Round-trip estimate used for net PnL and the margin reserve.</summary>
    public static ChargeBreakdown RoundTrip(ChargeRate rate, decimal entry, decimal exit, int quantity, decimal? brokerageOverride = null) =>
        ForOrder(rate, TxnType.Buy, entry, quantity, brokerageOverride) + ForOrder(rate, TxnType.Sell, exit, quantity, brokerageOverride);

    public static ChargeRate Resolve(IEnumerable<ChargeRate> rates, SecurityType type, DateOnly onDate) =>
        rates.Where(r => r.SecurityType == type && r.EffectiveFrom <= onDate).MaxBy(r => r.EffectiveFrom) ?? DefaultOptionRates;

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
