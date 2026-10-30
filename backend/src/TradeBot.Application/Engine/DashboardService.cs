using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

public sealed record AccountCard(
    long AccountId, string AccountName, string ClientId, string UserName, TradeMode Mode, bool IsDataProvider, bool IsActive,
    string TokenStatus, DateTime? TokenExpiryUtc, long? TokenSecondsLeft,
    decimal OpeningBalance, decimal AvailableMargin, decimal UtilisedMargin, decimal AllocatedCapital, DateTime? FundsSyncedAtUtc,
    decimal Balance, decimal BalanceChange, decimal BalanceChangePct, OtpPrompt? OtpPrompt = null);

public sealed record PnlSummary(
    decimal RealizedGross, decimal UnrealizedMtm, decimal Charges, decimal Net, int TotalTrades, int Wins, int Losses, decimal WinRatePct,
    decimal MaxDailyLoss, decimal LossLimitUsedPct, bool KillSwitch, bool ProfitLock, string? HaltReason);

public sealed record AccountPnl(long AccountId, string AccountName, TradeMode Mode, PnlSummary Pnl);

public sealed record TradeRow(
    long TradeId, long AccountId, string AccountName, TradeMode Mode, DateTime EntryTimeUtc, DateTime? ExitTimeUtc,
    string Contract, OptionType? OptType, int Quantity, int Lots, decimal EntryPrice, decimal StopLoss, decimal TrailingSl, decimal Target,
    decimal? Ltp, decimal? ExitPrice, decimal ProgressPct, TradeStatus Status, string? PatternCode, string? PatternName,
    string? NewsHeadline, MarketBias? NewsSentiment, decimal GrossPnl, decimal Charges, decimal NetPnl, decimal NetPnlPct,
    long SecurityId, long? InstrumentId, Timeframe? PatternTimeframe);

public sealed record BotStatus(
    DateTime ServerTimeUtc, string MarketSession, bool FeedConnected, DateTime? LastTickUtc, bool GlobalKillSwitch, string? GlobalReason,
    int OpenTrades, bool Prepared);

public sealed record DashboardOverview(BotStatus Bot, IReadOnlyList<AccountCard> Accounts, PnlSummary Consolidated, IReadOnlyList<AccountPnl> PerAccount, IReadOnlyList<TradeRow> Trades);

/// <summary>Builds the single-screen dashboard: DB rows (accounts, PnL ledger, trade book) overlaid with live engine state.</summary>
public sealed class DashboardService(ConfigCache config, TokenStore tokens, TradeManager trades, IMarketFeed feed, ITradingRepository trading,
    EngineOrchestrator orchestrator, OtpPrompts otp, TimeProvider clock)
{
    /// <summary><paramref name="scope"/>: "all", "real", "paper" or an account id.</summary>
    public async Task<DashboardOverview> BuildAsync(string scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = Ist.Today(clock);
        var accounts = InScope(scope);
        var ids = accounts.Select(a => a.AccountId).ToHashSet();

        var days = (await trading.DayPnlsAsync(today, ct)).Where(d => ids.Contains(d.AccountId)).ToDictionary(d => d.AccountId);
        var live = trades.OpenTrades.ToDictionary(t => t.Trade.TradeId);
        var rows = (await trading.TradesAsync(today, null, ct)).Where(t => ids.Contains(t.AccountId)).Select(t => Row(t, live.GetValueOrDefault(t.TradeId)?.Trade)).ToList();

        // MTM from the same rows the grid shows (live LTP for engine-managed trades, else last persisted LTP), net of entry charges
        var mtm = rows.Where(r => r.Status == TradeStatus.Open).GroupBy(r => r.AccountId).ToDictionary(g => g.Key, g => g.Sum(r => r.NetPnl));
        var perAccount = accounts.Select(a => new AccountPnl(a.AccountId, a.AccountName, a.TradeMode,
            Summary(days.GetValueOrDefault(a.AccountId), mtm.GetValueOrDefault(a.AccountId), a.RiskConfig?.MaxDailyLoss ?? 0))).ToList();

        var cards = accounts.Select(a =>
        {
            var tok = tokens.Latest(a.AccountId);
            var status = tok switch
            {
                null when a.TradeMode == TradeMode.Paper && !a.IsDataProvider => "NOT_REQUIRED",
                null => "MISSING",
                { LoginStatus: LoginStatus.Failed } => "FAILED",
                { TokenExpiry: { } e } when e <= now => "EXPIRED",
                { TokenExpiry: { } e } when e - now <= FixedLimits.TokenExpiryBuffer => "EXPIRING",
                { LoginStatus: LoginStatus.Success } => "VALID",
                _ => "PENDING",
            };
            var r = a.RiskConfig;
            // Balance = opening balance + today's net (realized + MTM − charges), the same figure as the PnL panel
            var opening = days.GetValueOrDefault(a.AccountId)?.OpeningBalance ?? r?.OpeningBalance ?? 0;
            var change = perAccount.First(p => p.AccountId == a.AccountId).Pnl.Net;
            return new AccountCard(a.AccountId, a.AccountName, a.ClientId, a.UserName, a.TradeMode, a.IsDataProvider, a.IsActive, status,
                tok?.TokenExpiry, tok?.TokenExpiry is { } exp ? (long)Math.Max(0, (exp - now).TotalSeconds) : null,
                opening, r?.AvailableMargin ?? 0, r?.UtilisedMargin ?? 0, r?.AllocatedCapital ?? 0, r?.FundsSyncedAt,
                Math.Round(opening + change, 2), Math.Round(change, 2), opening == 0 ? 0 : Math.Round(change / opening * 100, 2), otp.For(a.AccountId));
        }).ToList();

        var consolidated = Combine(perAccount.Select(p => p.Pnl).ToList());

        var bot = await trading.GetOrCreateBotStateAsync(today, ct);
        var botStatus = new BotStatus(now, Session(now), feed.IsConnected, feed.LastTickUtc, bot.GlobalKillSwitchTriggered, bot.Reason, live.Count, orchestrator.Prepared);
        return new DashboardOverview(botStatus, cards, consolidated, perAccount, rows);
    }

    public List<TradingAccount> InScope(string scope) => config.Accounts.Where(a => scope switch
    {
        "real" => a.TradeMode == TradeMode.Real,
        "paper" => a.TradeMode == TradeMode.Paper,
        "all" or "" => true,
        _ => long.TryParse(scope, out var id) && a.AccountId == id,
    }).ToList();

    private string Session(DateTime nowUtc)
    {
        var ist = FixedLimits.ToIst(nowUtc);
        if (config.Calendar.SessionFor(DateOnly.FromDateTime(ist)) is not { } s) return "HOLIDAY";
        var t = TimeOnly.FromDateTime(ist);
        if (t < s.Open) return "PRE_OPEN";
        if (t >= s.CloseExclusive) return "CLOSED";
        return t > config.Strategy.EntryCutoffTime ? "ENTRY_CLOSED" : "OPEN";
    }

    private TradeRow Row(DailyTrade t, DailyTrade? live)
    {
        var ltp = live?.CurrentLtp ?? t.CurrentLtp;
        var trailing = live?.TrailingSlPrice ?? t.TrailingSlPrice;
        var open = t.TradeStatus == TradeStatus.Open;
        var reference = open ? ltp ?? t.EntryBuyPrice : t.ExitSellPrice ?? t.EntryBuyPrice;
        var net = open ? (reference - t.EntryBuyPrice) * t.Quantity - t.BrokerageAndCharges : t.NetPnl;
        var gross = open ? (reference - t.EntryBuyPrice) * t.Quantity : t.GrossPnl;
        var span = t.TargetSellPrice - trailing;
        var progress = span <= 0 ? 0 : Math.Clamp((reference - trailing) / span * 100, 0, 100);
        var cost = t.EntryBuyPrice * t.Quantity;
        return new TradeRow(t.TradeId, t.AccountId, config.Account(t.AccountId)?.AccountName ?? $"#{t.AccountId}", t.TradeMode, t.EntryTime, t.ExitTime,
            t.Security?.PmlSymbol ?? t.SecurityId.ToString(), t.Security?.OptType, t.Quantity, t.Lots, t.EntryBuyPrice, t.StopLossPrice, trailing, t.TargetSellPrice,
            ltp, t.ExitSellPrice, Math.Round(progress, 1), t.TradeStatus, t.Detection?.Pattern?.PatternCode, t.Detection?.Pattern?.PatternName,
            t.News?.Headline, t.News?.Sentiment, Math.Round(gross, 2), t.BrokerageAndCharges, Math.Round(net, 2), cost == 0 ? 0 : Math.Round(net / cost * 100, 2),
            t.SecurityId, t.Security?.InstrumentId, t.Detection?.Timeframe);
    }

    private static PnlSummary Summary(DailyAccountPnl? d, decimal unrealized, decimal maxLoss)
    {
        if (d is null) return new PnlSummary(0, unrealized, 0, unrealized, 0, 0, 0, 0, maxLoss, 0, false, false, null);
        var net = d.RealizedGrossPnl + unrealized - d.TotalCharges;
        var closed = d.WinningTrades + d.LosingTrades;
        return new PnlSummary(d.RealizedGrossPnl, unrealized, d.TotalCharges, net, d.TotalTrades, d.WinningTrades, d.LosingTrades,
            closed == 0 ? 0 : Math.Round(100m * d.WinningTrades / closed, 1), maxLoss,
            maxLoss == 0 ? 0 : Math.Round(Math.Max(0, -net) / maxLoss * 100, 2), d.KillSwitchTriggered, d.ProfitLockTriggered, d.HaltReason);
    }

    private static PnlSummary Combine(IReadOnlyList<PnlSummary> s)
    {
        var closed = s.Sum(x => x.Wins + x.Losses);
        var maxLoss = s.Sum(x => x.MaxDailyLoss);
        var net = s.Sum(x => x.Net);
        return new PnlSummary(s.Sum(x => x.RealizedGross), s.Sum(x => x.UnrealizedMtm), s.Sum(x => x.Charges), net, s.Sum(x => x.TotalTrades),
            s.Sum(x => x.Wins), s.Sum(x => x.Losses), closed == 0 ? 0 : Math.Round(100m * s.Sum(x => x.Wins) / closed, 1), maxLoss,
            maxLoss == 0 ? 0 : Math.Round(Math.Max(0, -net) / maxLoss * 100, 2), s.Count > 0 && s.All(x => x.KillSwitch), s.Any(x => x.ProfitLock), null);
    }
}
