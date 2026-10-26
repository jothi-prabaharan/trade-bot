using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Patterns;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.External.Paytm;
using TradeBot.Infrastructure.Persistence;
using TradeBot.Infrastructure.Repositories;
using TradeBot.Infrastructure.Security;
using TradeBot.Infrastructure.Seeding;

namespace TradeBot.Infrastructure.Tests;

/// <summary>Own database: universe reconciliation touches every stock row, so it must not share the main fixture.</summary>
public sealed class SeedDatabase : IAsyncLifetime, IDbContextFactory<TradeBotDbContext>
{
    public string ConnectionString { get; } = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TRADEBOT_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=tradebot_test;Username=tradebot;Password=tradebot") { Database = "tradebot_test_seed" }.ConnectionString;

    private readonly ISecretProtector _protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));

    public TradeBotDbContext CreateDbContext()
    {
        var b = new DbContextOptionsBuilder<TradeBotDbContext>();
        b.UseTradeBotNpgsql(ConnectionString).AddInterceptors(new AuditTimestampsInterceptor(TimeProvider.System));
        return new TradeBotDbContext(b.Options, _protector);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ReferenceDataSeeder Seeder() => new(this, new ConfigRepository(this), NullLogger<ReferenceDataSeeder>.Instance);

    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "deploy", "seed"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }
}

public sealed class SeedingTests(SeedDatabase fx) : IClassFixture<SeedDatabase>
{
    private static readonly List<StockSeedRow> Bundled = ReferenceDataSeeder.ParseStocks(File.ReadAllText(SeedDatabase.RepoFile("deploy", "seed", "fno_universe.csv")));
    private static readonly List<HolidaySeedRow> Holidays = ReferenceDataSeeder.ParseHolidays(File.ReadAllText(SeedDatabase.RepoFile("deploy", "seed", "nse_holidays.csv")));

    [Fact]
    public void Bundled_universe_and_holidays_parse_and_weights_sum_to_100()
    {
        Assert.True(Bundled.Count >= 200);
        Assert.Equal(100m, Bundled.Sum(r => r.Nifty50WeightPct));
        Assert.Equal(50, Bundled.Count(r => r.Nifty50WeightPct > 0));
        Assert.Equal(14, Bundled.Count(r => r.BankniftyWeightPct > 0));
        Assert.True(Bundled.Max(r => r.BankniftyWeightPct) <= 20 && Bundled.OrderByDescending(r => r.BankniftyWeightPct).Take(3).Sum(r => r.BankniftyWeightPct) <= 45);
        Assert.Equal(15, Holidays.Count);
        Assert.All(Holidays, h => Assert.InRange(h.Date.DayOfWeek, DayOfWeek.Monday, DayOfWeek.Friday));
    }

    [Theory]
    [InlineData("symbol,company_name,sector,n,b,f\nAAA,A Ltd,X,50,0,0\nAAA,A2 Ltd,X,50,0,0", "Duplicate symbol AAA")]
    [InlineData("AAA,A Ltd,X,60,0,0\nBBB,B Ltd,X,20,0,0", "NIFTY 50 weights sum to 80")]
    [InlineData("AAA,A Ltd,X,101,0,0", "between 0 and 100")]
    [InlineData("AAA,A Ltd,X", "expected 6 columns")]
    [InlineData("AAA,,X,0,0,0", "company name are required")]
    public void Malformed_stock_files_are_rejected(string csv, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => ReferenceDataSeeder.ParseStocks(csv)).Message);

    [Fact]
    public async Task Seed_is_idempotent_and_keeps_dashboard_and_sync_managed_values()
    {
        await fx.Seeder().SeedAsync(Bundled, Holidays, CancellationToken.None);

        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(3, await db.Instruments.CountAsync(i => i.InstrumentType == InstrumentType.Index));
            Assert.True(await db.Instruments.CountAsync(i => i.InstrumentType == InstrumentType.Stock) >= Bundled.Count);
            Assert.Equal(PatternRegistry.All.Count, await db.ChartPatterns.CountAsync());
            Assert.True(PatternRegistry.All.Count >= 80);
            Assert.Equal(2, await db.ChargeRates.CountAsync());
            Assert.False((await db.ChartPatterns.FirstAsync(p => p.Category == PatternCategory.Harmonic)).IsActive);
            Assert.True(await db.GeminiNewsConfigs.AnyAsync(g => g.IsActive));
            Assert.True(await db.StrategyConfigs.AnyAsync(s => s.IsActive));
            var hdfc = await db.StockCompanies.Include(c => c.Instrument).SingleAsync(c => c.NseSymbol == "HDFCBANK");
            Assert.True(hdfc.BankniftyWeightPct > 0 && hdfc.IsFnoActive && hdfc.Instrument!.IsTradable);

            // What the dashboard and the scrip sync own
            var pattern = await db.ChartPatterns.FirstAsync(p => p.Category == PatternCategory.Candlestick);
            pattern.IsActive = false;
            var nifty = await db.Instruments.SingleAsync(i => i.Symbol == "NIFTY");
            nifty.LotSize = 75;
            nifty.FreezeQuantity = 1800;
            await db.SaveChangesAsync();
        }

        var reduced = Bundled.Where(r => r.Symbol != "YESBANK").Select(r => r.Symbol == "HDFCBANK" ? r with { BankniftyWeightPct = r.BankniftyWeightPct + 3m } : r).ToList();
        var second = await fx.Seeder().SeedAsync(reduced, Holidays, CancellationToken.None);
        Assert.Equal((0, 0, 0, 0), (second.IndicesAdded, second.StocksAdded, second.PatternsAdded, second.ChargeRatesAdded));
        Assert.Equal(1, second.WeightsCleared);

        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(75, (await db.Instruments.SingleAsync(i => i.Symbol == "NIFTY")).LotSize);
            Assert.Equal(PatternRegistry.All.Count - 1, await db.ChartPatterns.CountAsync(p => p.IsActive || p.Category == PatternCategory.Harmonic || p.Category == PatternCategory.SmcStructure));
            Assert.Equal(0m, (await db.StockCompanies.SingleAsync(c => c.NseSymbol == "YESBANK")).BankniftyWeightPct);
            Assert.Equal(15, await db.MarketHolidays.CountAsync());
        }
    }

    [Fact]
    public async Task Scrip_master_reconciles_the_fno_universe()
    {
        await fx.Seeder().SeedAsync(Bundled, [], CancellationToken.None);
        var sync = new ScripMasterSync(null!, null!, fx, Options.Create(new PaytmMoneyOptions()), NullLogger<ScripMasterSync>.Instance);
        var today = new DateOnly(2026, 10, 26);
        var expiry = new DateOnly(2026, 11, 24);
        static ScripRow Fut(string u, DateOnly e, int lot = 500) => new(Random.Shared.NextInt64(1, long.MaxValue), "NSE", "D", u, $"{u} FUT", SecurityType.FutStk, null, null, e, lot, 0.1m, lot * 20);

        // Live derivatives for every bundled stock except CANBK, plus a brand-new F&O name
        var rows = Bundled.Where(r => r.Symbol != "CANBK").Select(r => Fut(r.Symbol, expiry)).Append(Fut("NEWLISTCO", expiry, 900)).ToList();
        var (added, removed, restored) = await sync.ReconcileStockUniverseAsync(rows, today, CancellationToken.None);
        Assert.Equal((1, 1, 0), (added, removed, restored));

        await using (var db = fx.CreateDbContext())
        {
            var fresh = await db.Instruments.SingleAsync(i => i.Symbol == "NEWLISTCO");
            Assert.Equal((900, 18000, true), (fresh.LotSize, fresh.FreezeQuantity, fresh.IsTradable));
            Assert.True((await db.StockCompanies.SingleAsync(c => c.NseSymbol == "NEWLISTCO")).IsFnoActive);
            var canbk = await db.StockCompanies.Include(c => c.Instrument).SingleAsync(c => c.NseSymbol == "CANBK");
            Assert.False(canbk.IsFnoActive);
            Assert.False(canbk.Instrument!.IsTradable);
        }

        // CANBK returns; an expired-only listing does not count as live
        rows = Bundled.Select(r => Fut(r.Symbol, r.Symbol == "VOLTAS" ? today.AddDays(-1) : expiry)).Append(Fut("NEWLISTCO", expiry)).ToList();
        Assert.Equal((0, 1, 1), await sync.ReconcileStockUniverseAsync(rows, today, CancellationToken.None));

        // A truncated file never empties the universe
        Assert.Equal((0, 0, 0), await sync.ReconcileStockUniverseAsync(rows.Take(10).ToList(), today, CancellationToken.None));
        await using (var db = fx.CreateDbContext())
            Assert.True(await db.StockCompanies.CountAsync(c => c.IsFnoActive) > 200);
    }
}
