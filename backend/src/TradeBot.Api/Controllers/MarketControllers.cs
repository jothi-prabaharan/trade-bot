using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradeBot.Api.Hosting;
using TradeBot.Application.Engine;
using TradeBot.Application.Market;
using TradeBot.Domain.Enums;

namespace TradeBot.Api.Controllers;

public sealed record InstrumentDto(long InstrumentId, string Symbol, string? Name, InstrumentType InstrumentType, int LotSize, decimal TickSize, bool IsTradable, bool IsActive);

public sealed record CandleDto(DateTime Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

[ApiController, Authorize, Route("api")]
public sealed class MarketController(ConfigCache config, CandleQueryService candles, PnlCurveService pnl, DashboardService dashboard, TimeProvider clock) : ControllerBase
{
    [HttpGet("instruments")]
    public IEnumerable<InstrumentDto> Instruments() => config.Instruments.OrderBy(i => i.InstrumentType).ThenBy(i => i.Symbol)
        .Select(i => new InstrumentDto(i.InstrumentId, i.Symbol, i.Name, i.InstrumentType, i.LotSize, i.TickSize, i.IsTradable, i.IsActive));

    /// <summary>Chart candles for an underlying (no securityId: its futures series) or one option contract.</summary>
    [HttpGet("candles")]
    public async Task<IActionResult> Candles([FromQuery] long instrumentId, [FromQuery] long? securityId, [FromQuery] string timeframe = "5m",
        [FromQuery] DateOnly? date = null, [FromQuery] int days = 1, CancellationToken ct = default)
    {
        Timeframe tf;
        try { tf = DbValue.FromDb<Timeframe>(timeframe); }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException) { return ApiResults.Invalid("FIELD_INVALID", "Unknown timeframe.", "timeframe"); }
        if (days is < 1 or > 10) return ApiResults.Invalid("FIELD_RANGE", "days must be between 1 and 10.", "days");

        var day = date ?? Ist.Today(clock);
        var bars = await candles.GetAsync(new SeriesKey(instrumentId, securityId), tf,
            Ist.ToUtc(day.AddDays(-(days - 1)), TimeOnly.MinValue), Ist.ToUtc(day, TimeOnly.MaxValue), ct);
        return Ok(bars.Select(b => new CandleDto(b.Time, b.Open, b.High, b.Low, b.Close, b.Volume)));
    }

    [HttpGet("dashboard/pnl-curve")]
    public Task<PnlCurve> PnlCurve([FromQuery] string scope = "all", CancellationToken ct = default) =>
        pnl.BuildAsync(dashboard.InScope(scope).Select(a => a.AccountId).ToHashSet(), ct);
}
