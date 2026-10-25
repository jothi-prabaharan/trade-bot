using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Trading;

/// <summary>Picks expiry and strike from mst.security_master per mst.strategy_config.</summary>
public static class ContractSelector
{
    public static DateOnly? SelectExpiry(IEnumerable<DateOnly> expiries, DateOnly today, ExpiryPolicy policy)
    {
        var future = expiries.Where(e => e >= today).Distinct().Order().ToList();
        if (future.Count == 0) return null;
        return policy switch
        {
            ExpiryPolicy.Nearest => future[0],
            ExpiryPolicy.NearestSkipExpiryDay => future.Where(e => e > today).Select(e => (DateOnly?)e).FirstOrDefault(),
            // Monthly = last listed expiry in its calendar month
            ExpiryPolicy.MonthlyOnly => future.GroupBy(e => (e.Year, e.Month)).Select(g => g.Max()).Order().First(),
            _ => future[0],
        };
    }

    /// <summary>Strike whose |delta| is closest to <paramref name="targetDelta"/>; deltas are supplied by the caller.</summary>
    public static SecurityMaster? SelectByDelta(IEnumerable<(SecurityMaster Contract, double Delta)> chain, double targetDelta = 0.5) =>
        chain.Where(c => c.Contract.IsActive).OrderBy(c => Math.Abs(Math.Abs(c.Delta) - targetDelta)).Select(c => c.Contract).FirstOrDefault();

    /// <summary>ATM by nearest strike to the underlying; ITM1/OTM1 move one listed strike; DeltaTarget is resolved by the caller.</summary>
    public static SecurityMaster? SelectStrike(IEnumerable<SecurityMaster> chain, decimal underlying, OptionType type, StrikeSelection selection)
    {
        var strikes = chain.Where(s => s.OptType == type && s.StrikePrice is not null && s.IsActive).OrderBy(s => s.StrikePrice).ToList();
        if (strikes.Count == 0) return null;
        var atm = strikes.Select((s, i) => (s, i)).MinBy(x => Math.Abs(x.s.StrikePrice!.Value - underlying)).i;
        // For calls ITM is a lower strike; for puts ITM is higher
        var shift = selection switch
        {
            StrikeSelection.Itm1 => type == OptionType.Ce ? -1 : 1,
            StrikeSelection.Otm1 => type == OptionType.Ce ? 1 : -1,
            _ => 0,
        };
        var idx = Math.Clamp(atm + shift, 0, strikes.Count - 1);
        return strikes[idx];
    }
}
