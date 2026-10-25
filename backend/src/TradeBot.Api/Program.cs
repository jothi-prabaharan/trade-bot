using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TradeBot.Api.Hosting;
using TradeBot.Application;
using TradeBot.Application.Engine;
using TradeBot.Infrastructure;
using TradeBot.Infrastructure.Persistence;

if (args is ["hash-password", ..])
{
    Console.Write("Password: ");
    var password = args.Length > 1 ? args[1] : Console.ReadLine() ?? "";
    Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var auth = config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

builder.Services.AddSingleton(auth);
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddTradeBotInfrastructure(config);
builder.Services.AddTradeBotEngine();
if (config.GetValue("Engine:Enabled", true)) builder.Services.AddHostedService<EngineHostedService>();

builder.Services.AddControllers().AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = auth.Issuer, ValidAudience = auth.Issuer, IssuerSigningKey = auth.SigningKey(),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
    };
    // Browsers cannot set headers on WebSockets; accept the token from the query string there only
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/ws") && ctx.Request.Query["access_token"].FirstOrDefault() is { } t) ctx.Token = t;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("login", w => { w.PermitLimit = 5; w.Window = TimeSpan.FromMinutes(1); w.QueueLimit = 0; });
    o.AddFixedWindowLimiter("mutations", w => { w.PermitLimit = 60; w.Window = TimeSpan.FromMinutes(1); w.QueueLimit = 0; });
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", true))
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<TradeBotDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseTradeBotErrors();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", (Microsoft.Extensions.Hosting.IHostEnvironment env) => Results.Ok(new { status = "ok", env = env.EnvironmentName, utc = DateTime.UtcNow })).AllowAnonymous();
app.Map("/ws/dashboard", (HttpContext ctx, DashboardService d, ILogger<DashboardService> log) => DashboardSocket.HandleAsync(ctx, d, log)).RequireAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
