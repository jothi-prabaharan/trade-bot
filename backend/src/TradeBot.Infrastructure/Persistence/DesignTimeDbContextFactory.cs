using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TradeBot.Infrastructure.Security;

namespace TradeBot.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef`. Connection from TRADEBOT_DB env var; secrets are never touched at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TradeBotDbContext>
{
    public TradeBotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TRADEBOT_DB")
            ?? "Host=localhost;Port=5432;Database=tradebot;Username=tradebot;Password=tradebot";
        var builder = new DbContextOptionsBuilder<TradeBotDbContext>();
        builder.UseTradeBotNpgsql(connectionString);
        return new TradeBotDbContext(builder.Options, new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
    }
}
