using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Trading;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

public sealed record GatewayOrderUpdate(long OrderId, OrderStatus Status, int FilledQuantity, decimal? AveragePrice, string? Message, string? Raw);

/// <summary>Executes trn.orders legs for one trade mode and reports fills/cancels through <see cref="Updates"/>.</summary>
public interface IExecutionGateway
{
    TradeMode Mode { get; }
    event Action<GatewayOrderUpdate>? Updates;
    Task<BrokerAck> PlaceAsync(TradingAccount account, Order order, CancellationToken ct);
    Task<BrokerAck> ModifyAsync(TradingAccount account, Order order, CancellationToken ct);
    Task<BrokerAck> CancelAsync(TradingAccount account, Order order, CancellationToken ct);
}

/// <summary>PAPER: orders rest on the in-process <see cref="PaperExchange"/> and fill on live ticks.</summary>
public sealed class PaperGateway(MarketDataHub hub, TimeProvider clock, decimal slippagePct = 0.25m) : IExecutionGateway
{
    private readonly PaperExchange _exchange = new(slippagePct);
    private readonly ConcurrentDictionary<long, long> _paperToOrder = new();
    private readonly ConcurrentDictionary<long, long> _orderToPaper = new();

    public TradeMode Mode => TradeMode.Paper;
    public event Action<GatewayOrderUpdate>? Updates;

    public Task<BrokerAck> PlaceAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var (paper, fill) = _exchange.Place(o.SecurityId, o.TxnType, o.Quantity, o.OrderType, o.Price, o.TriggerPrice, TickOf(o),
            hub.Last(o.SecurityId)?.Ltp, clock.GetUtcNow().UtcDateTime, account.RiskConfig?.SimulatedSlippagePct);
        _paperToOrder[paper.Id] = o.OrderId;
        _orderToPaper[o.OrderId] = paper.Id;
        if (fill is not null) Raise(o.OrderId, fill);
        return Task.FromResult(new BrokerAck(true, $"PAPER-{paper.Id}", "Accepted", "{}"));
    }

    public Task<BrokerAck> ModifyAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var ok = _orderToPaper.TryGetValue(o.OrderId, out var id) && _exchange.Modify(id, o.Price, o.TriggerPrice, o.OrderType);
        if (ok && hub.Last(o.SecurityId) is { } t) foreach (var f in _exchange.OnTick(o.SecurityId, t.Ltp, clock.GetUtcNow().UtcDateTime)) Raise(_paperToOrder[f.OrderId], f);
        return Task.FromResult(new BrokerAck(ok, o.BrokerOrderNo, ok ? "Modified" : "Order not working", "{}"));
    }

    public Task<BrokerAck> CancelAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var ok = _orderToPaper.TryGetValue(o.OrderId, out var id) && _exchange.Cancel(id);
        if (ok) Updates?.Invoke(new GatewayOrderUpdate(o.OrderId, OrderStatus.Cancelled, 0, null, "Cancelled", null));
        return Task.FromResult(new BrokerAck(ok, o.BrokerOrderNo, ok ? "Cancelled" : "Order not working", "{}"));
    }

    /// <summary>Called by the feed pump for every tick.</summary>
    public void OnTick(long securityId, decimal ltp, DateTime nowUtc)
    {
        foreach (var f in _exchange.OnTick(securityId, ltp, nowUtc))
            if (_paperToOrder.TryGetValue(f.OrderId, out var orderId)) Raise(orderId, f);
    }

    private void Raise(long orderId, PaperFill f) =>
        Updates?.Invoke(new GatewayOrderUpdate(orderId, OrderStatus.Filled, f.Quantity, f.Price, "Simulated fill", null));

    private decimal TickOf(Order o) => hub.Info(o.SecurityId)?.Contract?.TickSize ?? 0.05m;
}

/// <summary>REAL: Paytm Money orders; status changes come from polling the order book (no order-update stream in the API).</summary>
public sealed class RealGateway(IPaytmMoneyClient client, TokenStore tokens, ILogger<RealGateway> log) : IExecutionGateway
{
    private readonly ConcurrentDictionary<string, (long OrderId, long AccountId, BrokerOrder? Last)> _tracked = new();

    public TradeMode Mode => TradeMode.Real;
    public event Action<GatewayOrderUpdate>? Updates;

    public bool HasWorkingOrders => !_tracked.IsEmpty;

    public async Task<BrokerAck> PlaceAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var token = tokens.AccessToken(account.AccountId) ?? throw new InvalidOperationException($"No valid access token for account {account.AccountId}.");
        var ack = await client.PlaceOrderAsync(token, Request(o), ct);
        if (ack is { Success: true, OrderNo: { } no }) _tracked[no] = (o.OrderId, account.AccountId, null);
        return ack;
    }

    public async Task<BrokerAck> ModifyAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var token = tokens.AccessToken(account.AccountId) ?? throw new InvalidOperationException("No valid access token.");
        return await client.ModifyOrderAsync(token, await RefAsync(account, o, ct), Request(o), ct);
    }

    public async Task<BrokerAck> CancelAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var token = tokens.AccessToken(account.AccountId) ?? throw new InvalidOperationException("No valid access token.");
        return await client.CancelOrderAsync(token, await RefAsync(account, o, ct), Request(o), ct);
    }

    /// <summary>Re-registers an order after a restart so polling resumes.</summary>
    public void Track(long accountId, Order o)
    {
        if (o.BrokerOrderNo is { } no) _tracked.TryAdd(no, (o.OrderId, accountId, null));
    }

    /// <summary>One polling pass over accounts with working REAL orders; raises updates for changed orders.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        foreach (var accountId in _tracked.Values.Select(v => v.AccountId).Distinct().ToList())
        {
            if (tokens.ReadToken(accountId) is not { } token) continue;
            IReadOnlyList<BrokerOrder> book;
            try { book = await client.GetOrdersAsync(token, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Order book poll failed for account {Account}", accountId);
                continue;
            }
            foreach (var b in book)
            {
                if (!_tracked.TryGetValue(b.Ref.OrderNo, out var t) || t.AccountId != accountId) continue;
                var changed = t.Last is null || t.Last.Status != b.Status || t.Last.FilledQuantity != b.FilledQuantity;
                _tracked[b.Ref.OrderNo] = t with { Last = b };
                if (!changed) continue;
                if (b.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected) _tracked.TryRemove(b.Ref.OrderNo, out _);
                Updates?.Invoke(new GatewayOrderUpdate(t.OrderId, b.Status, b.FilledQuantity, b.AveragePrice, b.Message, b.Raw));
            }
        }
    }

    private async Task<BrokerOrderRef> RefAsync(TradingAccount account, Order o, CancellationToken ct)
    {
        var no = o.BrokerOrderNo ?? throw new InvalidOperationException($"Order {o.OrderId} has no broker order number.");
        if (_tracked.TryGetValue(no, out var t) && t.Last is not null) return t.Last.Ref;
        if (tokens.ReadToken(account.AccountId) is { } read && (await client.GetOrdersAsync(read, ct)).FirstOrDefault(b => b.Ref.OrderNo == no) is { } fresh)
            return fresh.Ref;
        return new BrokerOrderRef(no);
    }

    private static BrokerOrderRequest Request(Order o) =>
        new(o.TxnType, o.SecurityId, o.Quantity, o.OrderType, o.Price, o.OrderType == OrderType.StopLossLimit ? o.TriggerPrice : null, o.Product, o.Segment);
}
