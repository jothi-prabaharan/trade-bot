using Microsoft.EntityFrameworkCore;
using TradeBot.Infrastructure;
using TradeBot.Infrastructure.Persistence;
using TradeBot.Infrastructure.Seeding;

namespace TradeBot.Api.Hosting;

/// <summary>
/// Operational commands of the API binary (used by deploy.sh and tradebotctl):
///   hash-password [password]                     bcrypt hash for Auth:AdminPasswordHash
///   migrate                                      apply pending EF Core migrations
///   seed [--stocks file.csv] [--holidays file.csv] idempotent reference data
/// Configuration comes from appsettings.json next to the binary plus environment variables, like the service.
/// </summary>
public static class Cli
{
    public static bool IsCommand(string[] args) => args is ["hash-password", ..] or ["migrate", ..] or ["seed", ..];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args[0] == "hash-password")
        {
            Console.Write("Password: ");
            var password = args.Length > 1 ? args[1] : Console.ReadLine() ?? "";
            if (password.Length < 10)
            {
                Console.Error.WriteLine("Use at least 10 characters.");
                return 2;
            }
            Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory, Args = args[1..] });
        // EF logs its probe of a not-yet-created history table as an error; harmless on a fresh database
        builder.Logging.SetMinimumLevel(LogLevel.Warning).AddFilter("TradeBot", LogLevel.Information)
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        builder.Services.AddTradeBotInfrastructure(builder.Configuration);
        using var host = builder.Build();
        var ct = CancellationToken.None;

        if (args[0] == "migrate")
        {
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<TradeBotDbContext>>().CreateDbContextAsync(ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);
            Console.WriteLine(pending.Count == 0 ? "Database is up to date." : $"Applied {pending.Count} migration(s): {string.Join(", ", pending)}");
            return 0;
        }

        var stocksFile = Option(args, "--stocks");
        var holidaysFile = Option(args, "--holidays");
        try
        {
            var stocks = stocksFile is null ? [] : ReferenceDataSeeder.ParseStocks(await File.ReadAllTextAsync(stocksFile, ct));
            var holidays = holidaysFile is null ? [] : ReferenceDataSeeder.ParseHolidays(await File.ReadAllTextAsync(holidaysFile, ct));
            var r = await host.Services.GetRequiredService<ReferenceDataSeeder>().SeedAsync(stocks, holidays, ct);
            Console.WriteLine($"Seeded: {r.IndicesAdded} indices and {r.StocksAdded} stocks added, {r.CompaniesUpdated} companies updated, " +
                $"{r.WeightsCleared} weights cleared, {r.PatternsAdded} patterns added, {r.HolidaysUpserted} holidays, {r.ChargeRatesAdded} charge rates.");
            return 0;
        }
        catch (Exception e) when (e is FormatException or IOException or ArgumentException)
        {
            Console.Error.WriteLine($"Seed failed: {e.Message}");
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
