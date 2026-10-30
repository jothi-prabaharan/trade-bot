using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Application.Engine;
using TradeBot.Application.Market;
using TradeBot.Application.News;

namespace TradeBot.Application;

public static class DependencyInjection
{
    /// <summary>The trading engine: one instance of each component per API+Engine process.</summary>
    public static IServiceCollection AddTradeBotEngine(this IServiceCollection services)
    {
        services.AddSingleton<ConfigCache>();
        services.AddSingleton<TokenStore>();
        services.AddSingleton<MarketDataHub>();
        services.AddSingleton(new BarHistory());
        services.AddSingleton(sp => new PaperGateway(sp.GetRequiredService<MarketDataHub>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<RealGateway>();
        services.AddSingleton(sp =>
        {
            var tm = ActivatorUtilities.CreateInstance<TradeManager>(sp);
            var accounts = sp.GetRequiredService<IAccountRepository>();
            var clock = sp.GetRequiredService<TimeProvider>();
            tm.UpdateFundsAsync = (id, available, utilised, ct) => accounts.UpdateFundsAsync(id, available, utilised, null, clock.GetUtcNow().UtcDateTime, ct);
            return tm;
        });
        services.AddSingleton<Dispatcher>();
        services.AddSingleton<UniverseService>();
        services.AddSingleton<SignalService>();
        services.AddSingleton<CandlePipeline>();
        services.AddSingleton<PatternScanner>();
        services.AddSingleton<HistoryBackfill>();
        services.AddSingleton<WatchlistBuilder>();
        services.AddSingleton<FundsSync>();
        services.AddSingleton<EngineOrchestrator>();
        services.AddSingleton<OtpPrompts>();
        services.TryAddSingleton<BrokerConnectionChecker>();
        services.AddSingleton<BrokerHealthMonitor>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<PnlCurveService>();
        services.AddSingleton<CandleQueryService>();
        return services;
    }

    /// <summary>TradeBot.Worker.News: Gemini scans and sentiment aggregation.</summary>
    public static IServiceCollection AddTradeBotNews(this IServiceCollection services) =>
        services.AddSingleton<NewsIntelligenceService>().AddSingleton<NewsScheduler>();

    /// <summary>TradeBot.Worker.Auth: daily headless broker logins.</summary>
    public static IServiceCollection AddTradeBotBrokerAuth(this IServiceCollection services)
    {
        services.TryAddSingleton<BrokerConnectionChecker>();
        return services.AddSingleton<BrokerAuthService>().AddSingleton<AuthScheduler>().AddSingleton<ISmsOtpPrompt, SmsOtpRelay>();
    }
}
