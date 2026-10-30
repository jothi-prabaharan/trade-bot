using Lib.Net.Http.WebPush;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeBot.Application.Abstractions;
using TradeBot.Infrastructure.External;
using TradeBot.Infrastructure.External.Gemini;
using TradeBot.Infrastructure.External.Paytm;
using TradeBot.Infrastructure.External.WebPush;
using TradeBot.Infrastructure.Persistence;
using TradeBot.Infrastructure.Seeding;
using TradeBot.Infrastructure.Repositories;
using TradeBot.Infrastructure.Security;

namespace TradeBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTradeBotPersistence(this IServiceCollection services, string connectionString, string masterKeyBase64)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector>(AesGcmSecretProtector.FromBase64(masterKeyBase64));
        services.AddSingleton<AuditTimestampsInterceptor>();
        // Factory for background workers; also registers a scoped TradeBotDbContext for controllers
        services.AddDbContextFactory<TradeBotDbContext>((sp, options) =>
            options.UseTradeBotNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditTimestampsInterceptor>()));
        return services;
    }

    public static DbContextOptionsBuilder UseTradeBotNpgsql(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", Schemas.Mst))
            .UseSnakeCaseNamingConvention();

    public static string ConnectionString(IConfiguration config) =>
        config.GetConnectionString("TradeBot") ?? Environment.GetEnvironmentVariable("TRADEBOT_DB")
        ?? throw new InvalidOperationException("Set ConnectionStrings:TradeBot or TRADEBOT_DB.");

    public static string MasterKey(IConfiguration config) =>
        config["Security:MasterKey"] ?? Environment.GetEnvironmentVariable("TRADEBOT_MASTER_KEY")
        ?? throw new InvalidOperationException("Set Security:MasterKey or TRADEBOT_MASTER_KEY (base64, 32 bytes).");

    /// <summary>Repositories and external adapters shared by the API+Engine, News and Auth hosts.</summary>
    public static IServiceCollection AddTradeBotInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = ConnectionString(config);
        services.AddTradeBotPersistence(cs, MasterKey(config));

        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<IConfigRepository, ConfigRepository>();
        services.AddSingleton<IMarketRepository, MarketRepository>();
        services.AddSingleton<IIntelligenceRepository, IntelligenceRepository>();
        services.AddSingleton<ITradingRepository, TradingRepository>();

        services.Configure<PaytmMoneyOptions>(config.GetSection("PaytmMoney"));
        services.Configure<BrokerLoginOptions>(config.GetSection("BrokerLogin"));

        // Long-lived clients with pooled-connection rotation (singletons must not capture factory clients)
        services.AddSingleton<IPaytmMoneyClient>(sp => new PaytmMoneyClient(Http(TimeSpan.FromSeconds(30)),
            sp.GetRequiredService<IOptions<PaytmMoneyOptions>>(), sp.GetRequiredService<ILogger<PaytmMoneyClient>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<INewsIntelligenceClient>(sp => new GeminiNewsClient(Http(TimeSpan.FromSeconds(180)), sp.GetRequiredService<ILogger<GeminiNewsClient>>()));
        var pushSubject = WebPushService.ResolveSubject(config);
        services.AddSingleton<IWebPushService>(sp => new WebPushService(
            new PushServiceClient(Http(TimeSpan.FromSeconds(15))) { AutoRetryAfter = true, MaxRetriesAfter = 1 },
            sp.GetRequiredService<IDbContextFactory<TradeBotDbContext>>(), sp.GetRequiredService<TimeProvider>(), pushSubject,
            sp.GetRequiredService<ILogger<WebPushService>>()));
        // Alerts are queued and fanned out to Telegram and Web Push in the background (see AlertDispatcher)
        services.AddSingleton(sp => new AlertDispatcher(
            [
                new TelegramAlertSender(Http(TimeSpan.FromSeconds(15)), sp.GetRequiredService<IDbContextFactory<TradeBotDbContext>>(),
                    sp.GetRequiredService<ILogger<TelegramAlertSender>>()),
                new WebPushAlertSender(sp.GetRequiredService<IWebPushService>()),
            ],
            sp.GetRequiredService<ILogger<AlertDispatcher>>()));
        services.AddSingleton<IAlertSender>(sp => sp.GetRequiredService<AlertDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<AlertDispatcher>());
        services.AddSingleton<IMarketFeed, PaytmMarketFeed>();
        services.AddSingleton<IScripMasterSync, ScripMasterSync>();
        services.AddSingleton<IBrokerLoginAutomation, PlaywrightBrokerLogin>();
        services.AddSingleton<ReferenceDataSeeder>();

        services.AddSingleton(sp => new PgEventBus(cs, sp.GetRequiredService<ILogger<PgEventBus>>()));
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<PgEventBus>());
        services.AddHostedService(sp => sp.GetRequiredService<PgEventBus>());
        return services;
    }

    private static HttpClient Http(TimeSpan timeout) =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = timeout };
}
