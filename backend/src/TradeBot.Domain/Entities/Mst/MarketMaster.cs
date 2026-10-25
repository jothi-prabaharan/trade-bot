using TradeBot.Domain.Common;
using TradeBot.Domain.Enums;

namespace TradeBot.Domain.Entities.Mst;

public class Instrument : AuditableEntity
{
    public long InstrumentId { get; set; }
    public required string Symbol { get; set; }
    public string? Name { get; set; }
    public InstrumentType InstrumentType { get; set; }
    public Exchange Exchange { get; set; } = Exchange.Nse;
    public Segment Segment { get; set; } = Segment.Derivatives;
    public int LotSize { get; set; }
    public decimal TickSize { get; set; }
    public decimal? StrikeStep { get; set; }
    public long? UnderlyingSecurityId { get; set; }
    public bool HasWeeklyExpiry { get; set; }
    public bool IsTradable { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<SecurityMaster> Securities { get; set; } = [];
}

public class StockCompany : AuditableEntity
{
    public long CompanyId { get; set; }
    public long? InstrumentId { get; set; }
    public required string NseSymbol { get; set; }
    public required string CompanyName { get; set; }
    public string? Sector { get; set; }
    public string? Industry { get; set; }
    public bool IsFnoActive { get; set; }
    public decimal Nifty50WeightPct { get; set; }
    public decimal BankniftyWeightPct { get; set; }
    public decimal FinniftyWeightPct { get; set; }
    public bool IsActive { get; set; } = true;

    public Instrument? Instrument { get; set; }
}

public class SecurityMaster
{
    public long SecurityId { get; set; }
    public long InstrumentId { get; set; }
    public Exchange Exchange { get; set; } = Exchange.Nse;
    public Segment Segment { get; set; } = Segment.Derivatives;
    public required string PmlSymbol { get; set; }
    public SecurityType SecurityType { get; set; }
    public decimal? StrikePrice { get; set; }
    public OptionType? OptType { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public int LotSize { get; set; }
    public decimal TickSize { get; set; }
    public bool IsActive { get; set; } = true;
    public DateOnly SyncedOn { get; set; }

    public Instrument Instrument { get; set; } = null!;
}

public class ChartPattern : AuditableEntity
{
    public long PatternId { get; set; }
    public required string PatternCode { get; set; }
    public required string PatternName { get; set; }
    public PatternCategory Category { get; set; }
    public PatternDirection Direction { get; set; }
    public int MinCandlesRequired { get; set; }
    public decimal DefaultWeight { get; set; }
    public bool RequireVolumeConfirm { get; set; }
    public bool RequireBreakoutClose { get; set; }
    public string PatternParameters { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}

public class ChargeRate : AuditableEntity
{
    public long ChargeRateId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public SecurityType SecurityType { get; set; }
    public decimal BrokeragePerOrder { get; set; }
    public decimal SttSellPct { get; set; }
    public decimal ExchangeTxnPct { get; set; }
    public decimal SebiFeePct { get; set; }
    public decimal StampDutyBuyPct { get; set; }
    public decimal GstPct { get; set; }
}
