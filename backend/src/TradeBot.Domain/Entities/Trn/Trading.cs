using TradeBot.Domain.Common;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Domain.Entities.Trn;

public class Order : AuditableEntity
{
    public long OrderId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long AccountId { get; set; }
    public long SignalId { get; set; }
    public long? ParentOrderId { get; set; }
    public OrderLegType OrderLegType { get; set; }
    public TradeMode TradeMode { get; set; }
    public Guid IdempotencyKey { get; set; } = Guid.CreateVersion7();
    public string? BrokerOrderNo { get; set; }
    public string? ExchangeOrderId { get; set; }
    public TxnType TxnType { get; set; }
    public Segment Segment { get; set; } = Segment.Derivatives;
    public ProductType Product { get; set; } = ProductType.Intraday;
    public long SecurityId { get; set; }
    public int Quantity { get; set; }
    public int FilledQuantity { get; set; }
    public decimal Price { get; set; }
    public decimal? TriggerPrice { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal TargetSellPrice { get; set; }
    public decimal OrderLtp { get; set; }
    public decimal? AvgFillPrice { get; set; }
    public OrderType OrderType { get; set; } = OrderType.Limit;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string? RejectionReason { get; set; }
    public string? RawResponse { get; set; }
    public DateTime? PlacedAt { get; set; }
    public DateTime? FilledAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public uint Version { get; set; }

    public TradingAccount Account { get; set; } = null!;
    public Signal Signal { get; set; } = null!;
    public Order? ParentOrder { get; set; }
    public ICollection<Order> ChildOrders { get; set; } = [];
    public SecurityMaster Security { get; set; } = null!;
}

public class DailyTrade : AuditableEntity
{
    public long TradeId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long AccountId { get; set; }
    public TradeMode TradeMode { get; set; }
    public long SignalId { get; set; }
    public long SecurityId { get; set; }
    public long DetectionId { get; set; }
    public long? NewsId { get; set; }
    public long EntryOrderId { get; set; }
    public long? SlOrderId { get; set; }
    public long? TargetOrderId { get; set; }
    public long? ExitOrderId { get; set; }
    public int Quantity { get; set; }
    public int Lots { get; set; }
    public decimal EntryBuyPrice { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal TrailingSlPrice { get; set; }
    public decimal TargetSellPrice { get; set; }
    public decimal? PeakLtp { get; set; }
    public decimal? CurrentLtp { get; set; }
    public decimal? ExitSellPrice { get; set; }
    public TradeStatus TradeStatus { get; set; } = TradeStatus.Open;
    public decimal GrossPnl { get; set; }
    public decimal BrokerageAndCharges { get; set; }
    public decimal NetPnl { get; private set; }
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public uint Version { get; set; }

    public TradingAccount Account { get; set; } = null!;
    public Signal Signal { get; set; } = null!;
    public SecurityMaster Security { get; set; } = null!;
    public DetectedPattern Detection { get; set; } = null!;
    public CompanyDailyNews? News { get; set; }
    public Order EntryOrder { get; set; } = null!;
    public Order? SlOrder { get; set; }
    public Order? TargetOrder { get; set; }
    public Order? ExitOrder { get; set; }
}

public class DailyAccountPnl : IHasUpdatedAt
{
    public long SummaryId { get; set; }
    public DateOnly TradingDate { get; set; }
    public long AccountId { get; set; }
    public TradeMode TradeMode { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal AvailableMargin { get; set; }
    public decimal UtilisedMargin { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal RealizedGrossPnl { get; set; }
    public decimal UnrealizedMtmPnl { get; set; }
    public decimal TotalCharges { get; set; }
    public decimal NetPnl { get; private set; }
    public decimal DailyLossLimitUsedPct { get; set; }
    public bool KillSwitchTriggered { get; set; }
    public DateTime? KillSwitchAt { get; set; }
    public bool ProfitLockTriggered { get; set; }
    public string? HaltReason { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }

    public TradingAccount Account { get; set; } = null!;
}

public class DailyBotState : IHasUpdatedAt
{
    public DateOnly TradingDate { get; set; }
    public bool GlobalKillSwitchTriggered { get; set; }
    public DateTime? TriggeredAt { get; set; }
    public string? TriggeredBy { get; set; }
    public string? Reason { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public class SystemEvent
{
    public long EventId { get; set; }
    public long? AccountId { get; set; }
    public required string EventType { get; set; }
    public EventSeverity Severity { get; set; } = EventSeverity.Info;
    public required string Message { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }

    public TradingAccount? Account { get; set; }
}
