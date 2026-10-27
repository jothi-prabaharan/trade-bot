using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TradeBot.Api.Hosting;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Infrastructure;
using TradeBot.Infrastructure.External.WebPush;
using TradeBot.Infrastructure.Persistence;
using TradeBot.Infrastructure.Security;

namespace TradeBot.Api.Tests;

public sealed class RecordingAlerts : IAlertSender
{
    private readonly List<(AlertKind Kind, string Html)> _sent = [];

    public IReadOnlyList<(AlertKind Kind, string Html)> Sent { get { lock (_sent) return _sent.ToList(); } }

    public Task SendAsync(AlertKind kind, string html, CancellationToken ct)
    {
        lock (_sent) _sent.Add((kind, html));
        return Task.CompletedTask;
    }
}

/// <summary>
/// The real API host on a throwaway PostgreSQL database (TRADEBOT_API_TEST_DB), engine loops off,
/// a controllable trading clock (Monday 10:00 IST) and recorded alerts.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminPassword = "e2e-Secret#1";
    public static readonly DateOnly TradingDay = new(2026, 11, 2);

    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("TRADEBOT_API_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=tradebot_api_test;Username=tradebot;Password=tradebot";

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(Ist.ToUtc(TradingDay, new TimeOnly(10, 0)), TimeSpan.Zero));
    public RecordingAlerts Alerts { get; } = new();
    public FakePushService Push { get; } = new();

    public ApiFactory()
    {
        // Program reads these while building the host, before WebApplicationFactory overrides apply
        Environment.SetEnvironmentVariable("ConnectionStrings__TradeBot", ConnectionString);
        Environment.SetEnvironmentVariable("Security__MasterKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Auth__JwtKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Auth__AdminPasswordHash", BCrypt.Net.BCrypt.HashPassword(AdminPassword, workFactor: 4));
        Environment.SetEnvironmentVariable("Engine__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton<IAlertSender>(Alerts);
            s.AddSingleton<IWebPushService>(sp => new WebPushService(new PushServiceClient(new HttpClient(Push)),
                sp.GetRequiredService<IDbContextFactory<TradeBotDbContext>>(), TimeProvider.System, "mailto:ops@example.com",
                sp.GetRequiredService<ILogger<WebPushService>>()));
            s.AddSingleton(sp => new JwtIssuer(sp.GetRequiredService<AuthOptions>(), TimeProvider.System));
        });
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<TradeBotDbContext>();
        options.UseTradeBotNpgsql(ConnectionString);
        await using (var db = new TradeBotDbContext(options.Options, new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32))))
            await db.Database.EnsureDeletedAsync();
        _ = Server; // starts the host: migrations run on startup
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<TradeBotDbContext> DbAsync() => await Get<IDbContextFactory<TradeBotDbContext>>().CreateDbContextAsync();

    private string? _token;

    /// <summary>Logs in once per run (the login endpoint allows 5 attempts a minute).</summary>
    public async Task<HttpClient> AuthorizedClientAsync()
    {
        var client = CreateClient();
        if (_token is null)
        {
            var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = AdminPassword });
            login.EnsureSuccessStatusCode();
            _token = (await login.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
