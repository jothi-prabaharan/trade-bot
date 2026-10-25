using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradeBot.Api.Hosting;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Validation;

namespace TradeBot.Api.Controllers;

[ApiController, Route("api/auth")]
public sealed class AuthController(AuthOptions options, JwtIssuer jwt) : ControllerBase
{
    public sealed record LoginRequest(string Username, string Password);

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public IActionResult Login(LoginRequest request)
    {
        var userOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Username ?? ""), Encoding.UTF8.GetBytes(options.AdminUser));
        var passOk = false;
        try { passOk = options.AdminPasswordHash is { } hash && BCrypt.Net.BCrypt.Verify(request.Password ?? "", hash); }
        catch (BCrypt.Net.SaltParseException) { /* misconfigured hash: treat as failure */ }
        if (!(userOk & passOk)) return Unauthorized(new { errors = new[] { new ApiError("AUTH_INVALID", "Invalid username or password.") } });
        var (token, expires) = jwt.Issue(options.AdminUser);
        return Ok(new { token, expiresAtUtc = expires, user = options.AdminUser });
    }
}

[ApiController, Authorize, Route("api/validation")]
public sealed class ValidationController : ControllerBase
{
    /// <summary>Rule catalog + form rules; Angular builds its validators from this.</summary>
    [HttpGet("rules")]
    public ContentResult Rules() =>
        Content(JsonSerializer.Serialize(ValidationRulesDocument.Build(), ValidationRulesDocument.Json), "application/json");
}

[ApiController, Authorize, Route("api/dashboard")]
public sealed class DashboardController(DashboardService dashboard) : ControllerBase
{
    /// <summary>Single unified overview: accounts + funds, consolidated and per-account PnL, today's trade book.</summary>
    [HttpGet("overview")]
    public Task<DashboardOverview> Overview([FromQuery] string scope = "all", CancellationToken ct = default) => dashboard.BuildAsync(scope, ct);
}

[ApiController, Authorize, Route("api/bot")]
public sealed class BotController(DashboardService dashboard, TradeManager trades) : ControllerBase
{
    public sealed record KillRequest(string Reason);

    [HttpGet("status")]
    public async Task<BotStatus> Status(CancellationToken ct) => (await dashboard.BuildAsync("all", ct)).Bot;

    /// <summary>Global emergency kill switch: squares off every account and halts trading for the day.</summary>
    [HttpPost("kill-switch"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> KillSwitch(KillRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiResults.Invalid("FIELD_REQUIRED", "A reason is required.", "reason");
        await trades.KillSwitchAsync(null, User.Identity?.Name ?? "admin", request.Reason, ct);
        return Accepted();
    }
}

[ApiController, Authorize, Route("api")]
public sealed class TradeController(ITradingRepository trading, IIntelligenceRepository intel, TradeManager trades, TimeProvider clock) : ControllerBase
{
    [HttpGet("trades")]
    public Task<List<Domain.Entities.Trn.DailyTrade>> Trades([FromQuery] DateOnly? date, [FromQuery] long? accountId, CancellationToken ct) =>
        trading.TradesAsync(date ?? Ist.Today(clock), accountId, ct);

    [HttpGet("trades/{tradeId:long}")]
    public async Task<IActionResult> Trade(long tradeId, CancellationToken ct)
    {
        if (await trading.TradeAsync(tradeId, ct) is not { } t) return NotFound();
        return Ok(new { trade = t, orders = await trading.OrdersForEntryAsync(t.EntryOrderId, ct) });
    }

    /// <summary>Dashboard "Manual Exit": cancels the bracket and exits with a marketable limit.</summary>
    [HttpPost("trades/{tradeId:long}/exit"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Exit(long tradeId) => await trades.ManualExitAsync(tradeId) ? Accepted() : NotFound();

    [HttpGet("signals")]
    public Task<List<Domain.Entities.Trn.Signal>> Signals([FromQuery] DateOnly? date, CancellationToken ct) => intel.SignalsAsync(date ?? Ist.Today(clock), ct);

    [HttpGet("events")]
    public Task<List<Domain.Entities.Trn.SystemEvent>> Events([FromQuery] int limit = 100, CancellationToken ct = default) => trading.EventsAsync(Math.Clamp(limit, 1, 1000), ct);
}
