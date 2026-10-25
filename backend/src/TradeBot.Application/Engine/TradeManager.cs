using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Trading;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

public sealed class LiveTrade
{
    public required DailyTrade Trade { get; init; }
    public required BracketState Bracket { get; init; }
    public required TradingAccount Account { get; init; }
    public required long InstrumentId { get; init; }
    public required Order Entry { get; init; }
    public Order? Sl { get; set; }
    public Order? Target { get; set; }
    public Order? Exit { get; set; }
    public string? Symbol { get; init; }
    public string? PatternName { get; init; }
    public string? NewsHeadline { get; init; }
    public DateTime? ExitPlacedAtUtc { get; set; }
    public int ExitAttempts { get; set; }
    public bool SquareOffIssued { get; set; }
    public object Gate { get; } = new();

    public IEnumerable<Order> Legs => new[] { Sl, Target, Exit }.OfType<Order>();
}

public sealed record EntryRequest(
    TradingAccount Account, Signal Signal, SizingResult Sizing, decimal QuoteLtp, long InstrumentId,
    string? Symbol, string? PatternName, string? NewsHeadline);

/// <summary>
/// Owns every open trade's OCO bracket. Tick decisions are made synchronously under the trade's lock;
/// broker and DB side effects run on a per-account queue so one account's calls never interleave.
/// </summary>
public sealed class TradeManager
{
    private static readonly OrderStatus[] WorkingStatuses = [OrderStatus.Pending, OrderStatus.Placed, OrderStatus.Open, OrderStatus.TriggerPending, OrderStatus.PartiallyFilled];
    private const int ExitRepriceSeconds = 3;

    private sealed class PendingEntry
    {
        public required Order Order { get; init; }
        public required EntryRequest Request { get; init; }
        public required DateTime PlacedAtUtc { get; init; }
        public bool CancelRequested { get; set; }
    }

    private readonly ConcurrentDictionary<long, LiveTrade> _trades = new();
    private readonly ConcurrentDictionary<long, PendingEntry> _entries = new();
    private readonly ConcurrentDictionary<long, long> _legToTrade = new();
    private readonly ConcurrentDictionary<long, Channel<Func<CancellationToken, Task>>> _queues = new();
    private readonly ConcurrentDictionary<long, decimal> _unrealized = new();
    private readonly ConcurrentDictionary<long, byte> _riskQueued = new();
    private readonly SemaphoreSlim _botLock = new(1, 1);
    private readonly ITradingRepository _repo;
    private readonly ConfigCache _config;
    private readonly MarketDataHub _hub;
    private readonly PaperGateway _paper;
    private readonly RealGateway _real;
    private readonly IAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<TradeManager> _log;
    private CancellationToken _stopping;
    private DateTime _lastPersistUtc;

    public TradeManager(ITradingRepository repo, ConfigCache config, MarketDataHub hub, PaperGateway paper, RealGateway real,
        IAlertSender alerts, TimeProvider clock, ILogger<TradeManager> log)
    {
        (_repo, _config, _hub, _paper, _real, _alerts, _clock, _log) = (repo, config, hub, paper, real, alerts, clock, log);
        _paper.Updates += OnOrderUpdate;
        _real.Updates += OnOrderUpdate;
    }

    public IReadOnlyCollection<LiveTrade> OpenTrades => _trades.Values.ToList();
    public int WorkingEntries(long accountId) => _entries.Values.Count(e => e.Request.Account.AccountId == accountId);
    public decimal Unrealized(long accountId) => _unrealized.GetValueOrDefault(accountId);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private IExecutionGateway Gateway(TradeMode mode) => mode == TradeMode.Real ? _real : _paper;

    // ---------------- lifecycle ----------------

    public async Task StartAsync(CancellationToken stopping)
    {
        _stopping = stopping;
        foreach (var t in await _repo.OpenTradesAsync(stopping))
        {
            if (_config.Account(t.AccountId) is not { } account) continue;
            var working = (await _repo.WorkingOrdersAsync(stopping)).Where(o => o.AccountId == t.AccountId).ToList();
            var entry = new Order { OrderId = t.EntryOrderId, AccountId = t.AccountId, SignalId = t.SignalId, SecurityId = t.SecurityId, TradeMode = t.TradeMode, OrderLegType = OrderLegType.Entry, Status = OrderStatus.Filled };
            var bracket = BracketState.FromRisk(t.TradeId, t.AccountId, t.SecurityId, t.TradeMode, t.Quantity, t.EntryBuyPrice, t.StopLossPrice, t.TargetSellPrice,
                t.Security.TickSize, account.RiskConfig!, _config.Strategy, t.TrailingSlPrice);
            bracket.PeakLtp = Math.Max(t.EntryBuyPrice, t.CurrentLtp ?? t.EntryBuyPrice);
            var live = new LiveTrade
            {
                Trade = t, Bracket = bracket, Account = account, InstrumentId = t.Security.InstrumentId, Entry = entry, Symbol = t.Security.PmlSymbol,
                PatternName = t.Detection?.Pattern?.PatternName, NewsHeadline = t.News?.Headline,
                Sl = working.FirstOrDefault(o => o.OrderId == t.SlOrderId),
                Target = working.FirstOrDefault(o => o.OrderId == t.TargetOrderId),
                Exit = working.FirstOrDefault(o => o.OrderLegType == OrderLegType.EmergencyExit && o.ParentOrderId == t.EntryOrderId),
            };
            Register(live);
            foreach (var leg in live.Legs.Where(IsWorking).Where(l => l.Status != OrderStatus.Pending))
            {
                if (t.TradeMode == TradeMode.Real) _real.Track(t.AccountId, leg);
                else Enqueue(t.AccountId, ct => _paper.PlaceAsync(account, leg, ct)); // paper book lives in memory
            }
            _log.LogInformation("Recovered open trade {Trade} ({Mode})", t.TradeId, t.TradeMode);
        }

        // Entries left working by a previous process have lost their sizing context: cancel them
        foreach (var o in (await _repo.WorkingOrdersAsync(stopping)).Where(o => o.OrderLegType == OrderLegType.Entry))
        {
            if (_config.Account(o.AccountId) is not { } account) continue;
            if (o.TradeMode == TradeMode.Real && o.BrokerOrderNo is not null)
            {
                _real.Track(o.AccountId, o);
                var ack = await _real.CancelAsync(account, o, stopping);
                if (!ack.Success)
                {
                    await EventAsync(o.AccountId, "ORPHAN_ENTRY", EventSeverity.Critical, $"Entry {o.OrderId} from a previous run could not be cancelled: {ack.Message}. Check the broker position.", stopping);
                    await _alerts.SendAsync(AlertKind.Critical, $"⚠️ Entry order {o.BrokerOrderNo} from before the restart could not be cancelled. Check the Paytm position.", stopping);
                    continue;
                }
            }
            o.Status = OrderStatus.Cancelled;
            o.CancelledAt = Now;
            o.RejectionReason = "Cancelled on engine restart";
            await _repo.UpdateOrderAsync(o, stopping);
        }
    }

    // ---------------- entry ----------------

    public Task<BrokerAck> SubmitEntryAsync(EntryRequest req)
    {
        var done = new TaskCompletionSource<BrokerAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(req.Account.AccountId, async ct =>
        {
            try { done.TrySetResult(await PlaceEntryAsync(req, ct)); }
            catch (Exception e) { done.TrySetException(e); throw; }
        });
        return done.Task;
    }

    private async Task<BrokerAck> PlaceEntryAsync(EntryRequest req, CancellationToken ct)
    {
        var s = req.Sizing;
        var order = await _repo.AddOrderAsync(new Order
        {
            TradingDate = Ist.Today(_clock), AccountId = req.Account.AccountId, SignalId = req.Signal.SignalId, OrderLegType = OrderLegType.Entry,
            TradeMode = req.Account.TradeMode, TxnType = TxnType.Buy, Product = _config.Strategy.Product, SecurityId = req.Signal.SecurityId,
            Quantity = s.Quantity, Price = s.LimitPrice, StopLossPrice = s.StopLossPrice, TargetSellPrice = s.TargetPrice,
            OrderLtp = req.QuoteLtp, OrderType = OrderType.Limit, Status = OrderStatus.Pending,
        }, ct);
        _entries[order.OrderId] = new PendingEntry { Order = order, Request = req, PlacedAtUtc = Now };

        var ack = await Gateway(order.TradeMode).PlaceAsync(req.Account, order, ct);
        order.BrokerOrderNo = ack.OrderNo;
        order.RawResponse = SafeJson(ack.Raw);
        order.PlacedAt = Now;
        if (order.Status == OrderStatus.Pending) order.Status = ack.Success ? OrderStatus.Placed : OrderStatus.Rejected;
        if (!ack.Success) order.RejectionReason = Trim(ack.Message ?? "Broker rejected the order");
        await _repo.UpdateOrderAsync(order, ct);

        if (!ack.Success)
        {
            _entries.TryRemove(order.OrderId, out _);
            await EventAsync(req.Account.AccountId, "ENTRY_REJECTED", EventSeverity.Warning, $"Entry rejected: {order.RejectionReason}", ct);
        }
        return ack;
    }

    // ---------------- order updates ----------------

    private void OnOrderUpdate(GatewayOrderUpdate u)
    {
        if (_entries.TryGetValue(u.OrderId, out var pending))
            Enqueue(pending.Request.Account.AccountId, ct => HandleEntryUpdateAsync(pending, u, ct));
        else if (_legToTrade.TryGetValue(u.OrderId, out var tradeId) && _trades.TryGetValue(tradeId, out var live))
            Enqueue(live.Account.AccountId, ct => HandleLegUpdateAsync(live, u, ct));
    }

    private async Task HandleEntryUpdateAsync(PendingEntry p, GatewayOrderUpdate u, CancellationToken ct)
    {
        var o = p.Order;
        var lot = Math.Max(1, p.Request.Sizing.Quantity / Math.Max(1, p.Request.Sizing.Lots));
        var filled = u.FilledQuantity / lot * lot;
        var terminal = u.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected;

        o.Status = u.Status;
        o.FilledQuantity = Math.Min(u.FilledQuantity, o.Quantity);
        o.AvgFillPrice = u.AveragePrice ?? o.AvgFillPrice;
        if (u.Status == OrderStatus.Filled) o.FilledAt = Now;
        if (u.Status == OrderStatus.Cancelled) o.CancelledAt = Now;
        if (u.Status == OrderStatus.Rejected) o.RejectionReason = Trim(u.Message ?? "Rejected");
        if (u.Raw is not null) o.RawResponse = SafeJson(u.Raw);
        await _repo.UpdateOrderAsync(o, ct);
        if (!terminal) return;

        _entries.TryRemove(o.OrderId, out _);
        if (filled <= 0)
        {
            await EventAsync(o.AccountId, "ENTRY_NOT_FILLED", EventSeverity.Info, $"Entry {o.OrderId} {u.Status}: {u.Message}", ct);
            return;
        }
        await OpenTradeAsync(p, filled, u.AveragePrice ?? o.Price, ct);
    }

    private async Task OpenTradeAsync(PendingEntry p, int quantity, decimal fillPrice, CancellationToken ct)
    {
        var req = p.Request;
        var account = req.Account;
        var risk = account.RiskConfig!;
        var s = req.Sizing;
        var info = _hub.Info(req.Signal.SecurityId);
        var tick = info?.Contract?.TickSize ?? 0.05m;
        var lot = info?.Contract?.LotSize ?? Math.Max(1, s.Quantity / Math.Max(1, s.Lots));

        if (fillPrice <= s.StopLossPrice)
        {
            // Filled through the stop in the same instant: flatten immediately rather than open an invalid bracket
            await PlaceRescueExitAsync(p.Order, quantity, fillPrice, tick, ct);
            return;
        }

        var sl = await _repo.AddOrderAsync(Leg(p.Order, OrderLegType.StopLoss, quantity, OrderType.StopLossLimit,
            ExitPricing.SlLimitPrice(s.StopLossPrice, _config.Strategy.SlLimitBufferPct, tick), s.StopLossPrice, s.StopLossPrice, s.TargetPrice, fillPrice), ct);
        await SendAsync(account, sl, ct);
        var target = await _repo.AddOrderAsync(Leg(p.Order, OrderLegType.TargetSell, quantity, OrderType.Limit, s.TargetPrice, null, s.StopLossPrice, s.TargetPrice, fillPrice), ct);

        var rate = ChargesCalculator.Resolve(_config.ChargeRates, info?.Contract?.SecurityType ?? SecurityType.OptIdx, Ist.Today(_clock));
        var entryCharges = ChargesCalculator.ForOrder(rate, TxnType.Buy, fillPrice, quantity, BrokerageOverride(account, p.Order.TradeMode)).Total;
        var trade = await _repo.AddTradeAsync(new DailyTrade
        {
            TradingDate = Ist.Today(_clock), AccountId = account.AccountId, TradeMode = p.Order.TradeMode, SignalId = req.Signal.SignalId,
            SecurityId = req.Signal.SecurityId, DetectionId = req.Signal.DetectionId, NewsId = req.Signal.NewsId,
            EntryOrderId = p.Order.OrderId, SlOrderId = sl.OrderId, TargetOrderId = target.OrderId,
            Quantity = quantity, Lots = quantity / lot, EntryBuyPrice = fillPrice, StopLossPrice = s.StopLossPrice, TrailingSlPrice = s.StopLossPrice,
            TargetSellPrice = s.TargetPrice, CurrentLtp = fillPrice, PeakLtp = fillPrice, BrokerageAndCharges = entryCharges, EntryTime = Now,
        }, ct);

        var bracket = BracketState.FromRisk(trade.TradeId, account.AccountId, trade.SecurityId, trade.TradeMode, quantity, fillPrice, s.StopLossPrice, s.TargetPrice, tick, risk, _config.Strategy);
        var live = new LiveTrade
        {
            Trade = trade, Bracket = bracket, Account = account, InstrumentId = req.InstrumentId, Entry = p.Order, Sl = sl, Target = target,
            Symbol = req.Symbol, PatternName = req.PatternName, NewsHeadline = req.NewsHeadline,
        };
        Register(live);
        if (sl.Status == OrderStatus.Rejected)
        {
            await _alerts.SendAsync(AlertKind.Critical, $"⚠️ Stop-loss order rejected for new trade {trade.TradeId}: {sl.RejectionReason}. Exiting the position.", ct);
            await ForceExitInternalAsync(live, TradeStatus.KillSwitchExit, ct);
        }

        if (trade.TradeMode == TradeMode.Paper) await AdjustPaperFundsAsync(account, -(fillPrice * quantity) - entryCharges, fillPrice * quantity, ct);
        await _alerts.SendAsync(AlertKind.Entry, Alerts.Entry(live), ct);
        await EventAsync(account.AccountId, "TRADE_OPENED", EventSeverity.Info, $"Trade {trade.TradeId} opened at {fillPrice} (SL {s.StopLossPrice}, target {s.TargetPrice})", ct);
    }

    private async Task PlaceRescueExitAsync(Order entry, int quantity, decimal fillPrice, decimal tick, CancellationToken ct)
    {
        var account = _config.Account(entry.AccountId) ?? throw new InvalidOperationException("Account missing");
        var price = ExitPricing.AggressiveSell(_hub.Last(entry.SecurityId)?.Ltp ?? fillPrice, _config.Strategy.SlLimitBufferPct, tick);
        var sl = Math.Max(tick, Math.Min(entry.StopLossPrice, price) - tick);
        var exit = await _repo.AddOrderAsync(Leg(entry, OrderLegType.EmergencyExit, quantity, OrderType.Limit, price, null, sl, Math.Max(entry.TargetSellPrice, sl + tick), fillPrice), ct);
        await SendAsync(account, exit, ct);
        await _alerts.SendAsync(AlertKind.Critical, $"⚠️ Entry {entry.OrderId} filled at {fillPrice} at/below its stop {entry.StopLossPrice}; flattening immediately.", ct);
        await EventAsync(entry.AccountId, "ENTRY_FILLED_THROUGH_SL", EventSeverity.Critical, $"Entry {entry.OrderId} filled at {fillPrice} <= SL {entry.StopLossPrice}", ct);
    }

    private async Task HandleLegUpdateAsync(LiveTrade live, GatewayOrderUpdate u, CancellationToken ct)
    {
        var leg = live.Legs.FirstOrDefault(l => l.OrderId == u.OrderId);
        if (leg is null) return;
        leg.Status = u.Status;
        leg.FilledQuantity = Math.Min(u.FilledQuantity, leg.Quantity);
        leg.AvgFillPrice = u.AveragePrice ?? leg.AvgFillPrice;
        if (u.Status == OrderStatus.Filled) leg.FilledAt = Now;
        if (u.Status == OrderStatus.Cancelled) leg.CancelledAt = Now;
        if (u.Status == OrderStatus.Rejected) leg.RejectionReason = Trim(u.Message ?? "Rejected");
        if (u.Raw is not null) leg.RawResponse = SafeJson(u.Raw);
        await _repo.UpdateOrderAsync(leg, ct);

        switch (u.Status)
        {
            case OrderStatus.Filled:
                TradeStatus reason;
                lock (live.Gate) reason = leg.OrderLegType == OrderLegType.StopLoss ? OcoDecider.OnStopFilled(live.Bracket) : OcoDecider.OnExitFilled(live.Bracket);
                await CloseTradeAsync(live, leg, u.AveragePrice ?? leg.Price, reason, ct);
                break;
            case OrderStatus.Rejected when leg.OrderLegType == OrderLegType.StopLoss:
                await _alerts.SendAsync(AlertKind.Critical, $"⚠️ Stop-loss order rejected for trade {live.Trade.TradeId}: {u.Message}. Exiting the position.", ct);
                await ForceExitInternalAsync(live, TradeStatus.KillSwitchExit, ct);
                break;
            case OrderStatus.Rejected:
                live.ExitPlacedAtUtc = DateTime.MinValue; // re-price on the next second
                break;
        }
    }

    // ---------------- ticks & timers ----------------

    public void OnTick(Tick t)
    {
        foreach (var live in _trades.Values.Where(x => x.Trade.SecurityId == t.SecurityId))
        {
            OcoDecision d;
            lock (live.Gate)
            {
                live.Trade.CurrentLtp = t.Ltp;
                live.Trade.PeakLtp = Math.Max(live.Trade.PeakLtp ?? t.Ltp, t.Ltp);
                d = OcoDecider.OnTick(live.Bracket, t.Ltp, t.TimeUtc);
            }
            if (d.Action is OcoAction.None or OcoAction.StopTriggered) continue;
            Enqueue(live.Account.AccountId, ct => ApplyDecisionAsync(live, d, ct));
        }
        RecomputeUnrealized();
    }

    private async Task ApplyDecisionAsync(LiveTrade live, OcoDecision d, CancellationToken ct)
    {
        switch (d.Action)
        {
            case OcoAction.RaiseTrailing when live.Sl is { } sl && IsWorking(sl):
                var tick = live.Bracket.Tick;
                sl.TriggerPrice = d.Price;
                sl.StopLossPrice = d.Price!.Value;
                sl.Price = ExitPricing.SlLimitPrice(d.Price.Value, _config.Strategy.SlLimitBufferPct, tick);
                var ack = await Gateway(live.Trade.TradeMode).ModifyAsync(live.Account, sl, ct);
                if (!ack.Success) _log.LogWarning("Trailing SL modify failed for trade {Trade}: {Msg}", live.Trade.TradeId, ack.Message);
                await _repo.UpdateOrderAsync(sl, ct);
                live.Trade.TrailingSlPrice = d.Price.Value;
                await _repo.UpdateTradeAsync(live.Trade, ct);
                break;

            case OcoAction.ExitAtTarget:
                if (live.Target is not { Status: OrderStatus.Pending } target)
                {
                    await PlaceEmergencyExitAsync(live, d.Price!.Value, ct);
                    break;
                }
                if (!await CancelWorkingAsync(live, live.Sl, ct)) return; // SL may have filled first; its update closes the trade
                target.Price = d.Price!.Value;
                target.OrderLtp = live.Trade.CurrentLtp ?? target.OrderLtp;
                await SendAsync(live.Account, target, ct);
                live.ExitPlacedAtUtc = Now;
                break;

            case OcoAction.EmergencyExit:
                await PlaceEmergencyExitAsync(live, d.Price!.Value, ct);
                break;
        }
    }

    /// <summary>Called once per second: entry timeouts, exit re-pricing, square-off, risk limits, persistence.</summary>
    public async Task OnSecondAsync(CancellationToken ct)
    {
        var now = Now;
        var strategy = _config.Strategy;

        foreach (var p in _entries.Values.Where(p => !p.CancelRequested && (now - p.PlacedAtUtc).TotalSeconds >= strategy.EntryFillTimeoutSeconds))
        {
            p.CancelRequested = true;
            Enqueue(p.Request.Account.AccountId, async c =>
            {
                var ack = await Gateway(p.Order.TradeMode).CancelAsync(p.Request.Account, p.Order, c);
                if (!ack.Success) _log.LogInformation("Entry {Order} cancel after timeout not accepted: {Msg}", p.Order.OrderId, ack.Message);
            });
        }

        var istTime = Ist.Time(_clock);
        foreach (var live in _trades.Values)
        {
            if (istTime >= strategy.SquareOffTime && !live.SquareOffIssued)
            {
                live.SquareOffIssued = true;
                Enqueue(live.Account.AccountId, c => ForceExitInternalAsync(live, TradeStatus.TimeSquareOff, c));
                continue;
            }
            var exitLeg = live.Exit is { } e && IsWorking(e) ? e : live.Target is { } t && IsWorking(t) && t.Status != OrderStatus.Pending ? t : null;
            if (exitLeg is not null && live.ExitPlacedAtUtc is { } placed && (now - placed).TotalSeconds >= ExitRepriceSeconds)
            {
                live.ExitPlacedAtUtc = now;
                Enqueue(live.Account.AccountId, c => RepriceExitAsync(live, exitLeg, c));
            }
        }

        foreach (var account in _config.Accounts)
            if (_riskQueued.TryAdd(account.AccountId, 0))
                Enqueue(account.AccountId, async c =>
                {
                    try { await EvaluateAccountRiskAsync(account, c); }
                    finally { _riskQueued.TryRemove(account.AccountId, out _); }
                });
        await EvaluateGlobalAsync(ct);
        if ((now - _lastPersistUtc).TotalSeconds >= 5)
        {
            _lastPersistUtc = now;
            foreach (var live in _trades.Values) Enqueue(live.Account.AccountId, c => _repo.UpdateTradeAsync(live.Trade, c));
        }
    }

    private async Task RepriceExitAsync(LiveTrade live, Order leg, CancellationToken ct)
    {
        if (!IsWorking(leg) || live.Trade.CurrentLtp is not { } ltp) return;
        live.ExitAttempts++;
        leg.Price = ExitPricing.AggressiveSell(ltp, _config.Strategy.SlLimitBufferPct * (1 + live.ExitAttempts), live.Bracket.Tick);
        if (leg.Status == OrderStatus.Rejected) { await PlaceEmergencyExitAsync(live, leg.Price, ct); return; }
        var ack = await Gateway(live.Trade.TradeMode).ModifyAsync(live.Account, leg, ct);
        if (ack.Success) await _repo.UpdateOrderAsync(leg, ct);
    }

    // ---------------- forced exits ----------------

    public Task<bool> ManualExitAsync(long tradeId)
    {
        if (!_trades.TryGetValue(tradeId, out var live)) return Task.FromResult(false);
        Enqueue(live.Account.AccountId, ct => ForceExitInternalAsync(live, TradeStatus.ManualExit, ct));
        return Task.FromResult(true);
    }

    public void ExitAllForAccount(long accountId, TradeStatus reason)
    {
        foreach (var live in _trades.Values.Where(t => t.Account.AccountId == accountId))
            Enqueue(accountId, ct => ForceExitInternalAsync(live, reason, ct));
        foreach (var p in _entries.Values.Where(e => e.Request.Account.AccountId == accountId && !e.CancelRequested))
        {
            p.CancelRequested = true;
            Enqueue(accountId, ct => Gateway(p.Order.TradeMode).CancelAsync(p.Request.Account, p.Order, ct));
        }
    }

    private async Task ForceExitInternalAsync(LiveTrade live, TradeStatus reason, CancellationToken ct)
    {
        OcoDecision d;
        lock (live.Gate) d = OcoDecider.ForceExit(live.Bracket, reason, live.Trade.CurrentLtp ?? live.Trade.EntryBuyPrice);
        if (d.Action == OcoAction.EmergencyExit) await PlaceEmergencyExitAsync(live, d.Price!.Value, ct);
    }

    private async Task PlaceEmergencyExitAsync(LiveTrade live, decimal price, CancellationToken ct)
    {
        // Never let two sell legs work at once: a second sell would open a short position
        if (!await CancelWorkingAsync(live, live.Sl, ct)) return;
        if (!await CancelWorkingAsync(live, live.Target, ct)) return;
        if (live.Exit is { } prior && IsWorking(prior))
        {
            prior.Price = price;
            await Gateway(live.Trade.TradeMode).ModifyAsync(live.Account, prior, ct);
            await _repo.UpdateOrderAsync(prior, ct);
            return;
        }
        var t = live.Trade;
        var exit = await _repo.AddOrderAsync(Leg(live.Entry, OrderLegType.EmergencyExit, t.Quantity, OrderType.Limit, price, null, t.StopLossPrice, t.TargetSellPrice, t.CurrentLtp ?? t.EntryBuyPrice), ct);
        live.Exit = exit;
        _legToTrade[exit.OrderId] = t.TradeId;
        await SendAsync(live.Account, exit, ct);
        live.ExitPlacedAtUtc = Now;
    }

    /// <summary>True when the leg is not working any more (cancelled now, or never sent).</summary>
    private async Task<bool> CancelWorkingAsync(LiveTrade live, Order? leg, CancellationToken ct)
    {
        if (leg is null || !IsWorking(leg)) return true;
        if (leg.Status == OrderStatus.Pending && leg.BrokerOrderNo is null)
        {
            leg.Status = OrderStatus.Cancelled;
            leg.CancelledAt = Now;
            await _repo.UpdateOrderAsync(leg, ct);
            return true;
        }
        var ack = await Gateway(live.Trade.TradeMode).CancelAsync(live.Account, leg, ct);
        if (!ack.Success) return false;
        leg.Status = OrderStatus.Cancelled;
        leg.CancelledAt = Now;
        await _repo.UpdateOrderAsync(leg, ct);
        return true;
    }

    // ---------------- close & risk ----------------

    private async Task CloseTradeAsync(LiveTrade live, Order exitLeg, decimal exitPrice, TradeStatus reason, CancellationToken ct)
    {
        if (!_trades.TryRemove(live.Trade.TradeId, out _)) return;
        foreach (var other in live.Legs.Where(l => l.OrderId != exitLeg.OrderId)) await CancelWorkingAsync(live, other, ct);

        var t = live.Trade;
        var info = _hub.Info(t.SecurityId);
        var rate = ChargesCalculator.Resolve(_config.ChargeRates, info?.Contract?.SecurityType ?? SecurityType.OptIdx, t.TradingDate);
        var charges = ChargesCalculator.RoundTrip(rate, t.EntryBuyPrice, exitPrice, t.Quantity, BrokerageOverride(live.Account, t.TradeMode)).Total;
        t.ExitSellPrice = exitPrice;
        t.ExitTime = Now;
        t.CurrentLtp = exitPrice;
        t.TradeStatus = reason;
        t.GrossPnl = (exitPrice - t.EntryBuyPrice) * t.Quantity;
        t.BrokerageAndCharges = charges;
        await _repo.UpdateTradeAsync(t, ct);

        var day = await DayPnlAsync(live.Account, ct);
        day.RealizedGrossPnl += t.GrossPnl;
        day.TotalCharges += charges;
        day.TotalTrades++;
        if (t.GrossPnl - charges > 0) day.WinningTrades++; else day.LosingTrades++;
        await _repo.UpdateDayPnlAsync(day, ct);

        if (t.TradeMode == TradeMode.Paper)
        {
            var exitCharges = charges - ChargesCalculator.ForOrder(rate, TxnType.Buy, t.EntryBuyPrice, t.Quantity, BrokerageOverride(live.Account, t.TradeMode)).Total;
            await AdjustPaperFundsAsync(live.Account, exitPrice * t.Quantity - exitCharges, -(t.EntryBuyPrice * t.Quantity), ct);
        }
        RecomputeUnrealized();
        await _alerts.SendAsync(AlertKind.Exit, Alerts.Exit(live, t.NetPnl), ct);
        await EventAsync(t.AccountId, "TRADE_CLOSED", EventSeverity.Info, $"Trade {t.TradeId} {DbValue.ToDb(reason)} at {exitPrice}; net {t.NetPnl:0.00}", ct);
    }

    private async Task EvaluateAccountRiskAsync(TradingAccount account, CancellationToken ct)
    {
        var day = await _repo.DayPnlAsync(account.AccountId, Ist.Today(_clock), ct);
        if (day is null || account.RiskConfig is not { } risk) return;
        var snapshot = new DayRiskSnapshot(day.RealizedGrossPnl, day.TotalCharges, Unrealized(account.AccountId));
        var (action, used) = DailyRiskMonitor.Evaluate(snapshot, risk, day.KillSwitchTriggered, day.ProfitLockTriggered);

        var changed = day.UnrealizedMtmPnl != snapshot.UnrealizedMtm || day.DailyLossLimitUsedPct != used || action != DayRiskAction.None;
        day.UnrealizedMtmPnl = snapshot.UnrealizedMtm;
        day.DailyLossLimitUsedPct = used;
        if (action == DayRiskAction.KillSwitch)
        {
            day.KillSwitchTriggered = true;
            day.KillSwitchAt = Now;
            day.HaltReason = $"Daily loss limit ₹{risk.MaxDailyLoss:0} reached (net {snapshot.Net:0.00})";
            ExitAllForAccount(account.AccountId, TradeStatus.KillSwitchExit);
            await _alerts.SendAsync(AlertKind.Critical, $"🛑 Kill switch: {account.AccountName} — {day.HaltReason}. Positions squared off.", ct);
            await EventAsync(account.AccountId, "KILL_SWITCH", EventSeverity.Critical, day.HaltReason, ct);
        }
        else if (action == DayRiskAction.ProfitLock)
        {
            day.ProfitLockTriggered = true;
            day.HaltReason = $"Daily profit target ₹{risk.MaxDailyProfit:0} reached; no new entries";
            await _alerts.SendAsync(AlertKind.Info, $"🔒 Profit lock: {account.AccountName} net {snapshot.Net:0.00}.", ct);
            await EventAsync(account.AccountId, "PROFIT_LOCK", EventSeverity.Info, day.HaltReason, ct);
        }
        if (changed) await _repo.UpdateDayPnlAsync(day, ct);
    }

    private async Task EvaluateGlobalAsync(CancellationToken ct)
    {
        var today = Ist.Today(_clock);
        var days = await _repo.DayPnlsAsync(today, ct);
        var active = _config.Accounts.Select(a => a.AccountId).ToHashSet();
        if (active.Count == 0 || !active.All(id => days.Any(d => d.AccountId == id && d.KillSwitchTriggered))) return;
        await SetGlobalKillAsync(today, "risk-supervisor", "All active accounts hit their daily loss limit", ct);
    }

    private async Task SetGlobalKillAsync(DateOnly today, string by, string reason, CancellationToken ct)
    {
        await _botLock.WaitAsync(ct);
        try
        {
            var bot = await _repo.GetOrCreateBotStateAsync(today, ct);
            if (bot.GlobalKillSwitchTriggered) return;
            bot.GlobalKillSwitchTriggered = true;
            bot.TriggeredAt = Now;
            bot.TriggeredBy = by;
            bot.Reason = Trim(reason);
            await _repo.UpdateBotStateAsync(bot, ct);
            await EventAsync(null, "GLOBAL_KILL_SWITCH", EventSeverity.Critical, $"{by}: {reason}", ct);
        }
        finally { _botLock.Release(); }
    }

    /// <summary>Manual global or per-account kill switch from the dashboard.</summary>
    public async Task KillSwitchAsync(long? accountId, string by, string reason, CancellationToken ct)
    {
        foreach (var account in _config.Accounts.Where(a => accountId is null || a.AccountId == accountId))
        {
            Enqueue(account.AccountId, async c =>
            {
                var day = await DayPnlAsync(account, c);
                if (day.KillSwitchTriggered) return;
                day.KillSwitchTriggered = true;
                day.KillSwitchAt = Now;
                day.HaltReason = Trim($"Manual kill switch by {by}: {reason}");
                await _repo.UpdateDayPnlAsync(day, c);
            });
            ExitAllForAccount(account.AccountId, TradeStatus.KillSwitchExit);
        }
        if (accountId is null) await SetGlobalKillAsync(Ist.Today(_clock), by, reason, ct);
        else await EventAsync(accountId, "KILL_SWITCH", EventSeverity.Critical, $"Manual by {by}: {reason}", ct);
        await _alerts.SendAsync(AlertKind.Critical, $"🛑 Manual kill switch ({(accountId is null ? "all accounts" : $"account {accountId}")}) by {by}: {reason}", ct);
    }

    // ---------------- helpers ----------------

    private void Register(LiveTrade live)
    {
        _trades[live.Trade.TradeId] = live;
        foreach (var leg in live.Legs) _legToTrade[leg.OrderId] = live.Trade.TradeId;
    }

    private void RecomputeUnrealized()
    {
        foreach (var group in _trades.Values.GroupBy(t => t.Account.AccountId))
            _unrealized[group.Key] = group.Sum(t => ((t.Trade.CurrentLtp ?? t.Trade.EntryBuyPrice) - t.Trade.EntryBuyPrice) * t.Trade.Quantity);
        foreach (var id in _unrealized.Keys.Where(id => _trades.Values.All(t => t.Account.AccountId != id))) _unrealized[id] = 0;
    }

    private async Task SendAsync(TradingAccount account, Order leg, CancellationToken ct)
    {
        var ack = await Gateway(leg.TradeMode).PlaceAsync(account, leg, ct);
        leg.BrokerOrderNo = ack.OrderNo ?? leg.BrokerOrderNo;
        leg.RawResponse = SafeJson(ack.Raw);
        leg.PlacedAt = Now;
        if (leg.Status == OrderStatus.Pending) leg.Status = ack.Success ? (leg.OrderType == OrderType.StopLossLimit ? OrderStatus.TriggerPending : OrderStatus.Placed) : OrderStatus.Rejected;
        if (!ack.Success) leg.RejectionReason = Trim(ack.Message ?? "Broker rejected the order");
        await _repo.UpdateOrderAsync(leg, ct);
        if (!ack.Success && _legToTrade.TryGetValue(leg.OrderId, out var tradeId) && _trades.TryGetValue(tradeId, out var live))
            await HandleLegUpdateAsync(live, new GatewayOrderUpdate(leg.OrderId, OrderStatus.Rejected, 0, null, ack.Message, null), ct);
    }

    private Order Leg(Order entry, OrderLegType type, int qty, OrderType orderType, decimal price, decimal? trigger, decimal sl, decimal target, decimal ltp) => new()
    {
        TradingDate = Ist.Today(_clock), AccountId = entry.AccountId, SignalId = entry.SignalId, ParentOrderId = entry.OrderId, OrderLegType = type,
        TradeMode = entry.TradeMode, TxnType = TxnType.Sell, Product = _config.Strategy.Product, SecurityId = entry.SecurityId, Quantity = qty,
        Price = price, TriggerPrice = trigger, StopLossPrice = sl, TargetSellPrice = target, OrderLtp = ltp, OrderType = orderType, Status = OrderStatus.Pending,
    };

    private async Task<DailyAccountPnl> DayPnlAsync(TradingAccount account, CancellationToken ct) =>
        await _repo.GetOrCreateDayPnlAsync(account.AccountId, Ist.Today(_clock), account.TradeMode,
            account.RiskConfig?.OpeningBalance ?? 0, account.RiskConfig?.AvailableMargin ?? 0, ct);

    private async Task AdjustPaperFundsAsync(TradingAccount account, decimal availableDelta, decimal utilisedDelta, CancellationToken ct)
    {
        if (account.RiskConfig is not { } r) return;
        r.AvailableMargin = Math.Max(0, r.AvailableMargin + availableDelta);
        r.UtilisedMargin = Math.Max(0, r.UtilisedMargin + utilisedDelta);
        var day = await DayPnlAsync(account, ct);
        day.AvailableMargin = r.AvailableMargin;
        day.UtilisedMargin = r.UtilisedMargin;
        await _repo.UpdateDayPnlAsync(day, ct);
        await UpdateFundsAsync(account.AccountId, r.AvailableMargin, r.UtilisedMargin, ct);
    }

    /// <summary>Hook for persisting virtual PAPER margins (wired to IAccountRepository by the host).</summary>
    public Func<long, decimal, decimal, CancellationToken, Task> UpdateFundsAsync { get; set; } = (_, _, _, _) => Task.CompletedTask;

    private static decimal? BrokerageOverride(TradingAccount a, TradeMode mode) => mode == TradeMode.Paper ? a.RiskConfig?.SimulatedBrokeragePerOrder : null;

    private static bool IsWorking(Order o) => WorkingStatuses.Contains(o.Status);

    private Task EventAsync(long? accountId, string type, EventSeverity severity, string message, CancellationToken ct) =>
        _repo.AddEventAsync(new SystemEvent { AccountId = accountId, EventType = type, Severity = severity, Message = Trim(message, 1000) }, ct);

    private void Enqueue(long accountId, Func<CancellationToken, Task> work)
    {
        var q = _queues.GetOrAdd(accountId, id =>
        {
            var ch = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() => RunQueueAsync(id, ch.Reader));
            return ch;
        });
        q.Writer.TryWrite(work);
    }

    private async Task RunQueueAsync(long accountId, ChannelReader<Func<CancellationToken, Task>> reader)
    {
        await foreach (var work in reader.ReadAllAsync(_stopping))
        {
            try { await work(_stopping); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                _log.LogError(e, "Account {Account} execution step failed", accountId);
                try { await EventAsync(accountId, "EXECUTION_ERROR", EventSeverity.Critical, e.Message, _stopping); } catch { /* logging only */ }
            }
        }
    }

    private static string? SafeJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { using var _ = System.Text.Json.JsonDocument.Parse(raw); return raw; }
        catch (System.Text.Json.JsonException) { return System.Text.Json.JsonSerializer.Serialize(new { raw }); }
    }

    private static string Trim(string s, int max = 500) => s.Length <= max ? s : s[..max];
}
