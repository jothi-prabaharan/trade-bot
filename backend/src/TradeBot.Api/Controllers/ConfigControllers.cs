using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradeBot.Api.Hosting;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Patterns;
using TradeBot.Application.Validation;
using TradeBot.Application.Validation.Forms;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Api.Controllers;

[ApiController, Authorize, Route("api/patterns")]
public sealed class PatternController(IConfigRepository configs, IIntelligenceRepository intel, ConfigCache config, IEventBus bus, TimeProvider clock) : ControllerBase
{
    /// <summary>mst.chart_patterns joined with the engine's registry (a row without a detector never fires).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await configs.PatternsAsync(ct)).Select(p => new { pattern = p, hasDetector = PatternRegistry.ByCode.ContainsKey(p.PatternCode) }));

    [HttpPut("{id:long}"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Update(long id, [FromBody] JsonObject body, CancellationToken ct)
    {
        if (await configs.PatternAsync(id, ct) is not { } current) return NotFound();
        body.Remove("patternCode"); // codes bind rows to detectors
        var merged = Json.Merge(current, body);
        if (FormValidator.Validate(FormRuleCatalog.ChartPattern, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);
        var next = merged.Deserialize<ChartPattern>(FormValidator.EntityJson)!;
        current.PatternName = next.PatternName;
        current.DefaultWeight = next.DefaultWeight;
        current.MinCandlesRequired = next.MinCandlesRequired;
        current.RequireVolumeConfirm = next.RequireVolumeConfirm;
        current.RequireBreakoutClose = next.RequireBreakoutClose;
        current.PatternParameters = next.PatternParameters;
        current.IsActive = next.IsActive;
        await configs.UpdatePatternAsync(current, ct);
        await ReloadAsync(ct);
        return Ok(current);
    }

    /// <summary>Toggle a whole family (e.g. switch on Harmonic or SMC).</summary>
    [HttpPut("category/{category}"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> ToggleCategory(string category, [FromQuery] bool active, CancellationToken ct)
    {
        PatternCategory cat;
        try { cat = DbValue.FromDb<PatternCategory>(category.ToUpperInvariant()); }
        catch (ArgumentOutOfRangeException) { return ApiResults.Invalid("FIELD_ENUM", $"Unknown category {category}.", "category"); }
        var rows = (await configs.PatternsAsync(ct)).Where(p => p.Category == cat && p.IsActive != active).ToList();
        foreach (var p in rows)
        {
            p.IsActive = active;
            await configs.UpdatePatternAsync(p, ct);
        }
        await ReloadAsync(ct);
        return Ok(new { updated = rows.Count });
    }

    [HttpGet("detections")]
    public Task<List<Domain.Entities.Trn.DetectedPattern>> Detections([FromQuery] DateOnly? date, CancellationToken ct) =>
        intel.DetectionsAsync(date ?? Ist.Today(clock), ct);

    private async Task ReloadAsync(CancellationToken ct)
    {
        await config.ReloadAsync(ct);
        await bus.PublishAsync(EventChannels.ConfigChanged, "chart_patterns", ct);
    }
}

[ApiController, Authorize, Route("api/strategy-config")]
public sealed class StrategyController(IConfigRepository configs, ConfigCache config, IEventBus bus) : ControllerBase
{
    [HttpGet]
    public Task<StrategyConfig> Get(CancellationToken ct) => configs.StrategyAsync(ct);

    [HttpPut, EnableRateLimiting("mutations")]
    public async Task<IActionResult> Update([FromBody] JsonObject body, CancellationToken ct)
    {
        var current = await configs.StrategyAsync(ct);
        var merged = Json.Merge(current, body);
        if (FormValidator.Validate(FormRuleCatalog.StrategyConfig, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);
        var next = merged.Deserialize<StrategyConfig>(FormValidator.EntityJson)!;
        next.StrategyConfigId = current.StrategyConfigId;
        next.IsActive = true;
        next.CreatedAt = current.CreatedAt;
        await configs.SaveStrategyAsync(next, ct);
        await config.ReloadAsync(ct);
        await bus.PublishAsync(EventChannels.ConfigChanged, "strategy_config", ct);
        return Ok(next);
    }
}

[ApiController, Authorize, Route("api/market-holidays")]
public sealed class HolidaysController(IConfigRepository configs, ConfigCache config) : ControllerBase
{
    [HttpGet]
    public Task<List<MarketHoliday>> List(CancellationToken ct) => configs.HolidaysAsync(ct);

    [HttpPost, EnableRateLimiting("mutations")]
    public Task<IActionResult> Create([FromBody] JsonObject body, CancellationToken ct) => SaveAsync(new MarketHoliday { Description = "" }, body, ct);

    [HttpPut("{id:long}"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Update(long id, [FromBody] JsonObject body, CancellationToken ct) =>
        (await configs.HolidaysAsync(ct)).FirstOrDefault(h => h.HolidayId == id) is { } h ? await SaveAsync(h, body, ct) : NotFound();

    [HttpDelete("{id:long}"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await configs.DeleteHolidayAsync(id, ct);
        await config.ReloadAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult> SaveAsync(MarketHoliday current, JsonObject body, CancellationToken ct)
    {
        var merged = Json.Merge(current, body);
        if (FormValidator.Validate(FormRuleCatalog.MarketHoliday, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);
        var next = merged.Deserialize<MarketHoliday>(FormValidator.EntityJson)!;
        next.HolidayId = current.HolidayId;
        var saved = await configs.SaveHolidayAsync(next, ct);
        await config.ReloadAsync(ct);
        return Ok(saved);
    }
}

[ApiController, Authorize, Route("api/news")]
public sealed class NewsIntelligenceController(IConfigRepository configs, IIntelligenceRepository intel, IEventBus bus, ConfigCache config, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<List<Domain.Entities.Trn.CompanyDailyNews>> News([FromQuery] DateOnly? date, [FromQuery] long? instrumentId, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        intel.NewsAsync(date ?? Ist.Today(clock), instrumentId, Math.Clamp(limit, 1, 500), ct);

    [HttpGet("sentiment")]
    public Task<List<Domain.Entities.Trn.DailyMarketSentiment>> Sentiment([FromQuery] DateOnly? date, CancellationToken ct) =>
        intel.SentimentsAsync(date ?? Ist.Today(clock), ct);

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken ct) =>
        await configs.GeminiAsync(ct) is { } g ? Ok(new { config = g, hasApiKey = !string.IsNullOrEmpty(g.GeminiApiKey) }) : NotFound();

    /// <summary>The API key is write-only: send "geminiApiKey" to change it, omit it to keep the stored one.</summary>
    [HttpPut("config"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> UpdateConfig([FromBody] JsonObject body, CancellationToken ct)
    {
        var current = await configs.GeminiAsync(ct) ?? new GeminiNewsConfig { ModelName = "gemini-flash-latest" };
        var merged = Json.Merge(current, body);
        if (FormValidator.Validate(FormRuleCatalog.GeminiConfig, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);
        var next = merged.Deserialize<GeminiNewsConfig>(FormValidator.EntityJson)!;
        next.GeminiConfigId = current.GeminiConfigId;
        next.IsActive = true;
        next.CreatedAt = current.CreatedAt;
        next.GeminiApiKey = Json.Has(body, "geminiApiKey") ? Json.Str(body, "geminiApiKey") : current.GeminiApiKey;
        await configs.SaveGeminiAsync(next, ct);
        await config.ReloadAsync(ct);
        await bus.PublishAsync(EventChannels.ConfigChanged, "gemini_news_config", ct);
        return Ok(new { config = next, hasApiKey = !string.IsNullOrEmpty(next.GeminiApiKey) });
    }

    public sealed record ScanRequest(long? InstrumentId);

    /// <summary>Asks the News worker for an immediate grounded scan (one instrument or the whole market).</summary>
    [HttpPost("scan"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Scan(ScanRequest request, CancellationToken ct)
    {
        if (request.InstrumentId is { } id && config.Instrument(id) is null) return NotFound();
        await bus.PublishAsync(EventChannels.NewsCheckRequested, request.InstrumentId?.ToString() ?? "all", ct);
        return Accepted();
    }
}
