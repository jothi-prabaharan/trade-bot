using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradeBot.Api.Hosting;
using TradeBot.Application.Abstractions;

namespace TradeBot.Api.Controllers;

/// <summary>Web Push devices: the installed dashboard (or a browser) receives alerts as system notifications.</summary>
[ApiController, Authorize, Route("api/push")]
public sealed class PushController(IWebPushService push) : ControllerBase
{
    /// <summary>VAPID public key for PushManager.subscribe({ applicationServerKey }).</summary>
    [HttpGet("config")]
    public async Task<object> Config(CancellationToken ct) => new { publicKey = await push.PublicKeyAsync(ct) };

    [HttpGet("devices")]
    public Task<IReadOnlyList<PushDevice>> Devices(CancellationToken ct) => push.DevicesAsync(ct);

    /// <summary>Adds this browser, or updates it when the endpoint is already known.</summary>
    [HttpPost("devices"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Subscribe(PushSubscribeRequest request, CancellationToken ct)
    {
        try { return Ok(await push.SubscribeAsync(request, ct)); }
        catch (ArgumentException e) { return ApiResults.Invalid("PUSH_INVALID_SUBSCRIPTION", e.Message); }
    }

    [HttpPut("devices/{id:long}/kinds"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> UpdateKinds(long id, PushAlertKinds kinds, CancellationToken ct) =>
        await push.UpdateKindsAsync(id, kinds, ct) is { } device ? Ok(device) : NotFound();

    [HttpDelete("devices/{id:long}"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Remove(long id, CancellationToken ct) => await push.RemoveAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("devices/{id:long}/test"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Test(long id, CancellationToken ct)
    {
        var result = await push.SendAsync(AlertKind.Info, "TradeBot test", "Notifications work on this device.", id, ct);
        return result is { Sent: 0, Failed: 0, Removed: 0 } ? NotFound() : Ok(result);
    }
}
