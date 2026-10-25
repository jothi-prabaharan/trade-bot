using Microsoft.EntityFrameworkCore;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Infrastructure.Persistence.Conventions;
using TradeBot.Infrastructure.Security;

namespace TradeBot.Infrastructure.Persistence;

public static class Schemas
{
    public const string Mst = "mst";
    public const string Trn = "trn";
}

public class TradeBotDbContext(DbContextOptions<TradeBotDbContext> options, ISecretProtector protector) : DbContext(options)
{
    // mst
    public DbSet<TradingAccount> TradingAccounts => Set<TradingAccount>();
    public DbSet<AccountAuthToken> AccountAuthTokens => Set<AccountAuthToken>();
    public DbSet<AccountRiskConfig> AccountRiskConfigs => Set<AccountRiskConfig>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<StockCompany> StockCompanies => Set<StockCompany>();
    public DbSet<SecurityMaster> SecurityMaster => Set<SecurityMaster>();
    public DbSet<ChartPattern> ChartPatterns => Set<ChartPattern>();
    public DbSet<ChargeRate> ChargeRates => Set<ChargeRate>();
    public DbSet<GeminiNewsConfig> GeminiNewsConfigs => Set<GeminiNewsConfig>();
    public DbSet<StrategyConfig> StrategyConfigs => Set<StrategyConfig>();
    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();
    public DbSet<MarketHoliday> MarketHolidays => Set<MarketHoliday>();

    // trn
    public DbSet<CompanyDailyNews> CompanyDailyNews => Set<CompanyDailyNews>();
    public DbSet<DailyMarketSentiment> DailyMarketSentiments => Set<DailyMarketSentiment>();
    public DbSet<OhlcvCandle> OhlcvCandles => Set<OhlcvCandle>();
    public DbSet<DetectedPattern> DetectedPatterns => Set<DetectedPattern>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<DailyTrade> DailyTrades => Set<DailyTrade>();
    public DbSet<DailyAccountPnl> DailyAccountPnls => Set<DailyAccountPnl>();
    public DbSet<DailyBotState> DailyBotStates => Set<DailyBotState>();
    public DbSet<SystemEvent> SystemEvents => Set<SystemEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<decimal?>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradeBotDbContext).Assembly);
        modelBuilder.ApplyTradeBotConventions(protector);
    }
}
