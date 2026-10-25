using TradeBot.Application.Market;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Trading;

public sealed record BracketPolicy(decimal MaxSlPct, decimal DefaultSlPct, decimal DefaultTargetPct, decimal MinRiskReward, decimal SlippageBufferPct)
{
    public static BracketPolicy From(AccountRiskConfig r) => new(r.MaxSlPct, r.DefaultSlPct, r.DefaultTargetPct, r.MinRiskRewardRatio, r.SlippageBufferPct);
}

/// <summary>Option premium the underlying's structural SL/target imply (Black-76 repriced at constant IV).</summary>
public sealed record StructuralPremiums(decimal StopLoss, decimal Target, Greeks Greeks);

public sealed record PremiumBracket(decimal Entry, decimal StopLoss, decimal Target, SlMethod Method)
{
    public decimal RiskReward => Math.Round((Target - Entry) / (Entry - StopLoss), 4);
}

/// <summary>Hybrid mandatory bracket: structural (capped at max SL %) when it meets min RR, else fixed % fallback.</summary>
public static class OptionBracket
{
    public static StructuralPremiums? MapStructural(
        decimal premium, decimal futures, decimal strike, OptionType type, DateOnly expiry, DateTime nowUtc,
        decimal underlyingSl, decimal underlyingTarget)
    {
        var t = Black76.YearsToExpiry(expiry, nowUtc);
        var isCall = type == OptionType.Ce;
        if (Black76.ImpliedVol((double)premium, (double)futures, (double)strike, t, isCall) is not { } iv) return null;
        var greeks = Black76.Compute((double)futures, (double)strike, t, iv, isCall);
        var sl = (decimal)Black76.Price((double)underlyingSl, (double)strike, t, iv, isCall);
        var target = (decimal)Black76.Price((double)underlyingTarget, (double)strike, t, iv, isCall);
        return sl < premium && target > premium ? new StructuralPremiums(sl, target, greeks) : null;
    }

    /// <summary>Expected fill: LTP plus the account's slippage buffer, on tick (the price the validator checks).</summary>
    public static decimal EntryReference(decimal ltp, BracketPolicy p, decimal tick) => TickMath.RoundUp(ltp * (1 + p.SlippageBufferPct / 100), tick);

    public static PremiumBracket Compute(decimal ltp, StructuralPremiums? structural, BracketPolicy p, decimal tick)
    {
        var entry = EntryReference(ltp, p, tick);
        if (structural is not null)
        {
            var floor = entry * (1 - p.MaxSlPct / 100);
            var sl = TickMath.RoundUp(Math.Max(structural.StopLoss, floor), tick);
            var target = TickMath.RoundDown(structural.Target, tick);
            if (sl > 0 && sl < entry && target > entry && (target - entry) / (entry - sl) >= p.MinRiskReward)
                return new PremiumBracket(entry, sl, target, SlMethod.Structural);
        }

        var fsl = TickMath.RoundUp(entry * (1 - p.DefaultSlPct / 100), tick);
        // Target never below min RR after tick rounding
        var ftarget = TickMath.RoundUp(Math.Max(entry * (1 + p.DefaultTargetPct / 100), entry + p.MinRiskReward * (entry - fsl)), tick);
        return new PremiumBracket(entry, fsl, ftarget, SlMethod.FixedPctFallback);
    }
}
