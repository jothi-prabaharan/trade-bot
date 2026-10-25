using TradeBot.Application.Validation;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Trading;

public sealed class PaperOrder
{
    public required long Id { get; init; }
    public required long SecurityId { get; init; }
    public required TxnType Side { get; init; }
    public required int Quantity { get; init; }
    public required decimal Tick { get; init; }
    public required decimal SlippagePct { get; init; }
    public OrderType Type { get; set; }
    public decimal Price { get; set; }
    public decimal? Trigger { get; set; }
    public bool Triggered { get; set; }
    public OrderStatus Status { get; set; }
    public decimal? FillPrice { get; set; }
    public DateTime? FilledAtUtc { get; set; }
}

public sealed record PaperFill(long OrderId, long SecurityId, TxnType Side, decimal Price, int Quantity, DateTime TimeUtc);

/// <summary>
/// Simulated exchange for PAPER accounts, driven by live ticks: marketable limits fill at LTP ± slippage
/// (never through their limit), SL-L orders trigger at their trigger and then rest as limits — so gaps
/// behave like the real exchange and exercise the same OCO fallback path.
/// </summary>
public sealed class PaperExchange(decimal slippagePct)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, PaperOrder> _orders = new();
    private long _nextId = 1;

    public PaperOrder? Get(long id) { lock (_lock) return _orders.GetValueOrDefault(id); }

    public (PaperOrder Order, PaperFill? Fill) Place(long securityId, TxnType side, int qty, OrderType type, decimal price, decimal? trigger, decimal tick, decimal? lastLtp, DateTime nowUtc, decimal? orderSlippagePct = null)
    {
        lock (_lock)
        {
            var o = new PaperOrder
            {
                Id = _nextId++, SecurityId = securityId, Side = side, Quantity = qty, Tick = tick, SlippagePct = orderSlippagePct ?? slippagePct,
                Type = type, Price = price, Trigger = trigger,
                Status = type == OrderType.StopLossLimit ? OrderStatus.TriggerPending : OrderStatus.Open,
            };
            _orders[o.Id] = o;
            return (o, lastLtp is { } ltp ? TryFill(o, ltp, nowUtc) : null);
        }
    }

    public bool Modify(long id, decimal price, decimal? trigger, OrderType? type = null)
    {
        lock (_lock)
        {
            if (!_orders.TryGetValue(id, out var o) || !IsWorking(o)) return false;
            o.Price = price;
            o.Trigger = trigger;
            if (type is { } t) o.Type = t;
            return true;
        }
    }

    public bool Cancel(long id)
    {
        lock (_lock)
        {
            if (!_orders.TryGetValue(id, out var o) || !IsWorking(o)) return false;
            o.Status = OrderStatus.Cancelled;
            return true;
        }
    }

    public IReadOnlyList<PaperFill> OnTick(long securityId, decimal ltp, DateTime nowUtc)
    {
        lock (_lock)
        {
            var fills = new List<PaperFill>();
            foreach (var o in _orders.Values.Where(o => o.SecurityId == securityId && IsWorking(o)).ToList())
                if (TryFill(o, ltp, nowUtc) is { } f) fills.Add(f);
            return fills;
        }
    }

    private static bool IsWorking(PaperOrder o) => o.Status is OrderStatus.Open or OrderStatus.TriggerPending;

    private PaperFill? TryFill(PaperOrder o, decimal ltp, DateTime nowUtc)
    {
        if (o.Type == OrderType.StopLossLimit && !o.Triggered)
        {
            var hit = o.Side == TxnType.Sell ? ltp <= o.Trigger : ltp >= o.Trigger;
            if (!hit) return null;
            o.Triggered = true;
            o.Status = OrderStatus.Open;
        }

        decimal fill;
        if (o.Side == TxnType.Buy)
        {
            if (ltp > o.Price) return null;
            fill = Math.Min(o.Price, TickMath.RoundUp(ltp * (1 + o.SlippagePct / 100), o.Tick));
        }
        else
        {
            if (ltp < o.Price) return null;
            fill = Math.Max(o.Price, TickMath.RoundDown(ltp * (1 - o.SlippagePct / 100), o.Tick));
        }

        o.Status = OrderStatus.Filled;
        o.FillPrice = fill;
        o.FilledAtUtc = nowUtc;
        return new PaperFill(o.Id, o.SecurityId, o.Side, fill, o.Quantity, nowUtc);
    }
}
