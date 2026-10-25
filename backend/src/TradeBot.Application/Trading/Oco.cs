using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Trading;

public static class ExitPricing
{
    /// <summary>Exchange SL-L sell limit: <paramref name="bufferPct"/> below the trigger, on tick.</summary>
    public static decimal SlLimitPrice(decimal trigger, decimal bufferPct, decimal tick) =>
        Math.Max(tick, TickMath.RoundDown(trigger * (1 - bufferPct / 100), tick));

    /// <summary>Marketable sell limit for forced exits (gap fallback, square-off, kill switch, manual).</summary>
    public static decimal AggressiveSell(decimal ltp, decimal bufferPct, decimal tick) =>
        Math.Max(tick, TickMath.RoundDown(ltp * (1 - bufferPct / 100), tick));
}

public static class TrailingStop
{
    /// <summary>
    /// Breakeven once the peak covers <paramref name="breakevenPct"/> of entry→target, then steps of
    /// <paramref name="stepPct"/> of that distance. Never loosens, never reaches the target.
    /// </summary>
    public static decimal Next(decimal entry, decimal target, decimal peak, decimal current, decimal breakevenPct, decimal stepPct, decimal tick)
    {
        var distance = target - entry;
        if (distance <= 0) return current;
        var progress = (peak - entry) / distance;
        var be = breakevenPct / 100;
        if (progress < be) return current;
        var step = stepPct / 100;
        var steps = Math.Floor((progress - be) / step);
        var level = TickMath.RoundDown(entry + steps * step * distance, tick);
        return level > current && level < target ? level : current;
    }
}

public enum BracketPhase { Open, ExitPending, Closed }

/// <summary>Live in-memory state of one trade's OCO bracket.</summary>
public sealed class BracketState
{
    public required long TradeId { get; init; }
    public required long AccountId { get; init; }
    public required long SecurityId { get; init; }
    public required TradeMode Mode { get; init; }
    public required int Quantity { get; init; }
    public required decimal Entry { get; init; }
    public required decimal InitialSl { get; init; }
    public required decimal Target { get; init; }
    public required decimal Tick { get; init; }
    public required decimal BreakevenTriggerPct { get; init; }
    public required decimal TrailingStepPct { get; init; }
    public required decimal SlLimitBufferPct { get; init; }
    public required int GapFallbackSeconds { get; init; }

    public decimal TrailingSl { get; set; }
    public decimal? PeakLtp { get; set; }
    public decimal? LastLtp { get; set; }
    public DateTime? SlTriggeredAtUtc { get; set; }
    public BracketPhase Phase { get; set; } = BracketPhase.Open;
    public TradeStatus? ExitReason { get; set; }

    public TradeStatus StopReason => TrailingSl > InitialSl ? TradeStatus.TrailingSlHit : TradeStatus.SlHit;

    public static BracketState FromRisk(long tradeId, long accountId, long securityId, TradeMode mode, int qty, decimal entry, decimal sl, decimal target,
        decimal tick, AccountRiskConfig risk, StrategyConfig strategy, decimal? trailing = null) => new()
    {
        TradeId = tradeId, AccountId = accountId, SecurityId = securityId, Mode = mode, Quantity = qty,
        Entry = entry, InitialSl = sl, Target = target, Tick = tick, TrailingSl = trailing ?? sl,
        BreakevenTriggerPct = risk.BreakevenTriggerPct, TrailingStepPct = risk.TrailingSlStepPct,
        SlLimitBufferPct = strategy.SlLimitBufferPct, GapFallbackSeconds = strategy.SlGapFallbackSeconds,
    };
}

public enum OcoAction { None, RaiseTrailing, ExitAtTarget, StopTriggered, EmergencyExit }

public sealed record OcoDecision(OcoAction Action, decimal? Price = null, TradeStatus? Reason = null)
{
    public static readonly OcoDecision None = new(OcoAction.None);
}

/// <summary>
/// Mode-agnostic OCO decisions per tick. The exchange (real or paper) owns the resting SL-L order;
/// this decides when to trail it, when to take the target and when a gap needs an aggressive exit.
/// </summary>
public static class OcoDecider
{
    public static OcoDecision OnTick(BracketState s, decimal ltp, DateTime nowUtc)
    {
        if (s.Phase != BracketPhase.Open) return OcoDecision.None;
        s.LastLtp = ltp;

        if (s.SlTriggeredAtUtc is { } triggered)
        {
            if ((nowUtc - triggered).TotalSeconds < s.GapFallbackSeconds) return OcoDecision.None;
            s.Phase = BracketPhase.ExitPending;
            s.ExitReason = s.StopReason;
            return new OcoDecision(OcoAction.EmergencyExit, ExitPricing.AggressiveSell(ltp, s.SlLimitBufferPct, s.Tick), s.ExitReason);
        }

        if (ltp >= s.Target)
        {
            s.Phase = BracketPhase.ExitPending;
            s.ExitReason = TradeStatus.TargetHit;
            return new OcoDecision(OcoAction.ExitAtTarget, s.Target, TradeStatus.TargetHit);
        }

        if (ltp <= s.TrailingSl)
        {
            s.SlTriggeredAtUtc = nowUtc;
            return new OcoDecision(OcoAction.StopTriggered, s.TrailingSl, s.StopReason);
        }

        if (ltp > (s.PeakLtp ?? s.Entry))
        {
            s.PeakLtp = ltp;
            var next = TrailingStop.Next(s.Entry, s.Target, ltp, s.TrailingSl, s.BreakevenTriggerPct, s.TrailingStepPct, s.Tick);
            if (next > s.TrailingSl)
            {
                s.TrailingSl = next;
                return new OcoDecision(OcoAction.RaiseTrailing, next);
            }
        }
        return OcoDecision.None;
    }

    /// <summary>Time square-off, kill switch or manual exit.</summary>
    public static OcoDecision ForceExit(BracketState s, TradeStatus reason, decimal ltp)
    {
        if (s.Phase == BracketPhase.Closed) return OcoDecision.None;
        s.Phase = BracketPhase.ExitPending;
        s.ExitReason = reason;
        return new OcoDecision(OcoAction.EmergencyExit, ExitPricing.AggressiveSell(ltp, s.SlLimitBufferPct, s.Tick), reason);
    }

    /// <summary>The exchange filled the resting SL-L order before any software exit.</summary>
    public static TradeStatus OnStopFilled(BracketState s)
    {
        s.Phase = BracketPhase.Closed;
        s.ExitReason ??= s.StopReason;
        return s.ExitReason.Value;
    }

    public static TradeStatus OnExitFilled(BracketState s)
    {
        s.Phase = BracketPhase.Closed;
        return s.ExitReason ?? TradeStatus.ManualExit;
    }
}

public sealed record DayRiskSnapshot(decimal RealizedGross, decimal Charges, decimal UnrealizedMtm)
{
    public decimal Net => RealizedGross + UnrealizedMtm - Charges;
}

public enum DayRiskAction { None, ProfitLock, KillSwitch }

public static class DailyRiskMonitor
{
    public static (DayRiskAction Action, decimal LossUsedPct) Evaluate(DayRiskSnapshot s, AccountRiskConfig r, bool killed, bool locked)
    {
        var used = Math.Round(Math.Max(0, -s.Net) / r.MaxDailyLoss * 100, 2);
        if (!killed && s.Net <= -r.MaxDailyLoss) return (DayRiskAction.KillSwitch, used);
        if (!killed && !locked && s.Net >= r.MaxDailyProfit) return (DayRiskAction.ProfitLock, used);
        return (DayRiskAction.None, used);
    }
}
