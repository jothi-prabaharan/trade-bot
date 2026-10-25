using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Validation;

public static class ModeToggleValidator
{
    public static List<RuleViolation> Validate(TradingAccount account, int openTradeCount, TradeMode target, string? confirmClientId)
    {
        var v = new List<RuleViolation>();
        if (!Enum.IsDefined(target)) v.Add(RuleCatalog.Violation(RuleCodes.InvalidTradeMode, "targetMode"));
        if (account.TradeMode == target) v.Add(RuleCatalog.Violation(RuleCodes.ModeUnchanged, "targetMode"));
        if (openTradeCount > 0) v.Add(RuleCatalog.Violation(RuleCodes.ToggleBlockedOpenTrades));
        if (target != TradeMode.Real) return v;

        if (!account.IsActive) v.Add(RuleCatalog.Violation(RuleCodes.AccountInactive));
        if (string.IsNullOrEmpty(account.ApiKey) || string.IsNullOrEmpty(account.ApiSecret)) v.Add(RuleCatalog.Violation(RuleCodes.ApiCredentialsMissing));
        if (!string.Equals(confirmClientId?.Trim(), account.ClientId, StringComparison.Ordinal))
            v.Add(RuleCatalog.Violation(RuleCodes.RealConfirmationRequired, "confirmClientId"));
        return v;
    }
}

/// <summary>Pre-broker checks for a single order leg; mirrors the trn.orders CHECKs with readable codes.</summary>
public static class OrderValidator
{
    public static List<RuleViolation> Validate(Order o, SecurityMaster security, Instrument instrument)
    {
        var v = new List<RuleViolation>();
        var tick = security.TickSize;

        if (o.Segment != Segment.Derivatives) v.Add(RuleCatalog.Violation(RuleCodes.InvalidSegment, "segment"));
        if (o.StopLossPrice <= 0) v.Add(RuleCatalog.Violation(RuleCodes.SlNotPositive, "stopLossPrice"));
        if (o.TargetSellPrice <= o.StopLossPrice) v.Add(RuleCatalog.Violation(RuleCodes.TargetNotAboveEntry, "targetSellPrice"));

        if (o.OrderLegType == OrderLegType.Entry)
        {
            if (o.ParentOrderId is not null) v.Add(RuleCatalog.Violation(RuleCodes.ParentLinkInvalid, "parentOrderId"));
            if (o.TxnType != TxnType.Buy) v.Add(RuleCatalog.Violation(RuleCodes.EntryMustBeBuy, "txnType"));
            v.AddRange(BracketRules.Validate(o.OrderLtp, o.StopLossPrice, o.TargetSellPrice));
            v.AddRange(BracketRules.Validate(o.Price, o.StopLossPrice, o.TargetSellPrice));
        }
        else if (o.ParentOrderId is null || o.TxnType != TxnType.Sell)
            v.Add(RuleCatalog.Violation(RuleCodes.ParentLinkInvalid, "parentOrderId"));

        if ((o.OrderLegType == OrderLegType.StopLoss && o.OrderType != OrderType.StopLossLimit)
            || (o.OrderLegType == OrderLegType.TargetSell && o.OrderType != OrderType.Limit))
            v.Add(RuleCatalog.Violation(RuleCodes.LegOrderTypeMismatch, "orderType"));

        if (o.OrderType == OrderType.StopLossLimit
            && (o.TriggerPrice is not > 0 || (o.TxnType == TxnType.Sell && o.Price > o.TriggerPrice)))
            v.Add(RuleCatalog.Violation(RuleCodes.SlTriggerInvalid, "triggerPrice"));

        foreach (var (field, price) in new[] { ("price", (decimal?)o.Price), ("triggerPrice", o.TriggerPrice), ("stopLossPrice", o.StopLossPrice), ("targetSellPrice", o.TargetSellPrice) })
            if (price is { } p && !TickMath.IsOnTick(p, tick)) v.Add(RuleCatalog.Violation(RuleCodes.PriceNotOnTick, field));

        if (o.Quantity <= 0 || o.Quantity % security.LotSize != 0) v.Add(RuleCatalog.Violation(RuleCodes.QtyNotLotMultiple, "quantity"));
        if (o.Quantity > instrument.FreezeQuantity) v.Add(RuleCatalog.Violation(RuleCodes.QtyAboveFreeze, "quantity"));

        // Same rule reported twice (ltp and price) collapses to one per field
        return v.DistinctBy(x => (x.Code, x.Field)).ToList();
    }
}
