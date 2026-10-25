using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradeBot.Infrastructure.Persistence;
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
}
