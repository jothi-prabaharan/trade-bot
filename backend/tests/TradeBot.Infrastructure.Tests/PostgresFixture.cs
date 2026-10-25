using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure;
using TradeBot.Infrastructure.Persistence;
using TradeBot.Infrastructure.Security;

namespace TradeBot.Infrastructure.Tests;

/// <summary>Migrates a throwaway database (TRADEBOT_TEST_DB) once per test run.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("TRADEBOT_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=tradebot_test;Username=tradebot;Password=tradebot";

    public ISecretProtector Protector { get; } = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));

    public TradeBotDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<TradeBotDbContext>();
        builder.UseTradeBotNpgsql(ConnectionString).AddInterceptors(new AuditTimestampsInterceptor(TimeProvider.System));
        return new TradeBotDbContext(builder.Options, Protector);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Inserts the minimum mst/trn graph a trade needs and returns the ids.</summary>
    public async Task<SeedGraph> SeedAsync()
    {
        await using var db = CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var account = new TradingAccount { AccountName = $"Paper {suffix}", ClientId = $"PAPER-{suffix}", UserName = "Test User" };
        account.RiskConfig = new AccountRiskConfig();
        var instrument = new Instrument { Symbol = $"NIFTY-{suffix}", InstrumentType = InstrumentType.Index, LotSize = 65, FreezeQuantity = 1800, TickSize = 0.05m, StrikeStep = 50, IsTradable = true };
        var pattern = new ChartPattern { PatternCode = $"BULL_ENGULF_{suffix}", PatternName = "Bullish Engulfing", Category = PatternCategory.Candlestick, Direction = PatternDirection.Bullish, MinCandlesRequired = 2, DefaultWeight = 60 };
        db.AddRange(account, instrument, pattern);
        await db.SaveChangesAsync();

        var security = new SecurityMaster
        {
            SecurityId = Random.Shared.NextInt64(1, long.MaxValue), InstrumentId = instrument.InstrumentId, PmlSymbol = $"NIFTY 25000 CE {suffix}",
            SecurityType = SecurityType.OptIdx, StrikePrice = 25000, OptType = OptionType.Ce, ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
            LotSize = 65, TickSize = 0.05m, SyncedOn = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        var detection = new DetectedPattern
        {
            Instrument = instrument, Pattern = pattern, Timeframe = Timeframe.M5, TrendTimeframe = Timeframe.M15, TrendBias = MarketBias.Bullish,
            Direction = PatternDirection.Bullish, NeckOrBreakoutPrice = 25010, StructuralSlPrice = 24960, StructuralTargetPrice = 25110, ConfidenceScore = 78, IsConfirmed = true
        };
        db.AddRange(security, detection);
        await db.SaveChangesAsync();

        var signal = NewSignal(detection.DetectionId, instrument.InstrumentId, security.SecurityId, entry: 100, sl: 85, target: 130);
        db.Add(signal);
        await db.SaveChangesAsync();

        return new SeedGraph(account.AccountId, instrument.InstrumentId, security.SecurityId, detection.DetectionId, signal.SignalId);
    }

    public static Signal NewSignal(long detectionId, long instrumentId, long securityId, decimal entry, decimal sl, decimal target) => new()
    {
        DetectionId = detectionId, InstrumentId = instrumentId, SecurityId = securityId, SignalType = SignalType.Buy, SpotPrice = 25010, OptionDelta = 0.52m,
        EntryOptionLtp = entry, MandatorySlPrice = sl, MandatoryTargetPrice = target, SlMethod = SlMethod.Structural,
        PatternScore = 78, NewsScore = 65, OiGreeksScore = 60, CombinedAiConfidence = 71.5m
    };

    public static Order NewEntryOrder(SeedGraph g, decimal ltp = 100, decimal price = 100.5m, decimal sl = 85, decimal target = 130) => new()
    {
        AccountId = g.AccountId, SignalId = g.SignalId, OrderLegType = OrderLegType.Entry, TradeMode = TradeMode.Paper, TxnType = TxnType.Buy,
        SecurityId = g.SecurityId, Quantity = 65, Price = price, StopLossPrice = sl, TargetSellPrice = target, OrderLtp = ltp, OrderType = OrderType.Limit
    };
}

public sealed record SeedGraph(long AccountId, long InstrumentId, long SecurityId, long DetectionId, long SignalId);

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
