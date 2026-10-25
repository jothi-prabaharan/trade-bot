using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Application.Validation;

/// <summary>NSE session lookup built from mst.market_holidays.</summary>
public sealed class MarketCalendar(IEnumerable<MarketHoliday> holidays)
{
    private readonly Dictionary<DateOnly, MarketHoliday> _byDate = holidays.ToDictionary(h => h.HolidayDate);

    public static readonly MarketCalendar WeekendsOnly = new([]);

    /// <summary>Session bounds for the IST date, or null when the market is shut.</summary>
    public (TimeOnly Open, TimeOnly CloseExclusive)? SessionFor(DateOnly istDate)
    {
        if (_byDate.TryGetValue(istDate, out var h))
            return h.IsSpecialSession ? (h.SessionOpenTime!.Value, h.SessionCloseTime!.Value) : null;
        if (istDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        return (FixedLimits.MarketOpen, FixedLimits.MarketCloseExclusive);
    }

    public RuleViolation? CheckMarketOpen(DateTime nowUtc)
    {
        var ist = FixedLimits.ToIst(nowUtc);
        var session = SessionFor(DateOnly.FromDateTime(ist));
        if (session is null) return RuleCatalog.Violation(RuleCodes.NotTradingDay);
        var t = TimeOnly.FromDateTime(ist);
        return t >= session.Value.Open && t < session.Value.CloseExclusive ? null : RuleCatalog.Violation(RuleCodes.MarketClosed);
    }
}
