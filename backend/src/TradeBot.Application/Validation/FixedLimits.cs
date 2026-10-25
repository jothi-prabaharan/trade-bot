namespace TradeBot.Application.Validation;

/// <summary>Hard-coded rails. Values that tune behaviour live in mst.*; these are never editable at runtime.</summary>
public static class FixedLimits
{
    public static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);
    public static readonly TimeOnly MarketOpen = new(9, 15);
    public static readonly TimeOnly MarketCloseExclusive = new(15, 30); // last entry tick 15:29:59

    public static readonly TimeSpan MaxTickAge = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(15);

    /// <summary>Constituent news counts toward an index veto at or above this index weight.</summary>
    public const decimal NewsVetoMinConstituentWeightPct = 5m;

    /// <summary>Round-trip brokerage reserved when checking margin (entry + exit).</summary>
    public const int BrokerageOrdersPerTrade = 2;

    public static DateTime ToIst(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(IstOffset);
}
