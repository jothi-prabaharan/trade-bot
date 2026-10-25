namespace TradeBot.Application.Validation;

public static class TickMath
{
    public static decimal RoundUp(decimal price, decimal tick) => Math.Ceiling(price / tick) * tick;
    public static decimal RoundDown(decimal price, decimal tick) => Math.Floor(price / tick) * tick;
    public static bool IsOnTick(decimal price, decimal tick) => price % tick == 0;
}

/// <summary>Fixed mandatory SL / target rules for long option positions.</summary>
public static class BracketRules
{
    public static List<RuleViolation> Validate(decimal entry, decimal? stopLoss, decimal? target)
    {
        var v = new List<RuleViolation>();
        if (stopLoss is null) v.Add(RuleCatalog.Violation(RuleCodes.SlRequired, "stopLossPrice"));
        if (target is null) v.Add(RuleCatalog.Violation(RuleCodes.TargetRequired, "targetSellPrice"));
        if (stopLoss is <= 0) v.Add(RuleCatalog.Violation(RuleCodes.SlNotPositive, "stopLossPrice"));
        if (stopLoss > 0 && stopLoss >= entry) v.Add(RuleCatalog.Violation(RuleCodes.SlNotBelowEntry, "stopLossPrice"));
        if (target is not null && target <= entry) v.Add(RuleCatalog.Violation(RuleCodes.TargetNotAboveEntry, "targetSellPrice"));
        return v;
    }

    public static List<RuleViolation> ValidateTrailingUpdate(decimal currentTrailing, decimal newTrailing, decimal target, decimal tick)
    {
        var v = new List<RuleViolation>();
        if (newTrailing < currentTrailing) v.Add(RuleCatalog.Violation(RuleCodes.TrailingSlLoosened, "trailingSlPrice"));
        if (newTrailing >= target) v.Add(RuleCatalog.Violation(RuleCodes.TrailingSlAtTarget, "trailingSlPrice"));
        if (!TickMath.IsOnTick(newTrailing, tick)) v.Add(RuleCatalog.Violation(RuleCodes.PriceNotOnTick, "trailingSlPrice"));
        return v;
    }
}
