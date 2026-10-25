using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.External.Paytm;

/// <summary>
/// Downloads the scrip master and keeps only contracts of active mst.instruments: futures/options expiring
/// within 70 days, plus the underlying's spot row (index/equity) for strike selection.
/// </summary>
public sealed class ScripMasterSync(
    IPaytmMoneyClient client, IMarketRepository market, IDbContextFactory<TradeBotDbContext> dbFactory,
    IOptions<PaytmMoneyOptions> options, ILogger<ScripMasterSync> log) : IScripMasterSync
{
    public const int HorizonDays = 70;

    public async Task<int> SyncAsync(DateOnly today, CancellationToken ct)
    {
        var csv = await client.DownloadSecurityMasterAsync(options.Value.SecurityMasterFile, ct);
        var instruments = (await market.InstrumentsAsync(tradableOnly: false, ct)).ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        var horizon = today.AddDays(HorizonDays);

        var rows = SecurityMasterCsv.Parse(csv, options.Value.StrikeDivisor)
            .Where(r => r.Exchange == "NSE" && instruments.ContainsKey(r.Underlying))
            .Where(r => r.Type is SecurityType.Index or SecurityType.Equity || (r.Expiry is { } e && e >= today && e <= horizon))
            .ToList();

        var securities = rows.Select(r => new SecurityMaster
        {
            SecurityId = r.SecurityId,
            InstrumentId = instruments[r.Underlying].InstrumentId,
            Exchange = Exchange.Nse,
            Segment = r.Segment == "D" ? Segment.Derivatives : Segment.Equity,
            PmlSymbol = r.Symbol,
            SecurityType = r.Type,
            StrikePrice = r.Strike,
            OptType = r.OptType,
            ExpiryDate = r.Expiry,
            LotSize = r.LotSize,
            TickSize = r.TickSize,
            IsActive = true,
            SyncedOn = today,
        }).ToList();

        var count = await market.UpsertSecuritiesAsync(securities, ct);
        await UpdateInstrumentsAsync(rows, instruments, ct);
        log.LogInformation("Scrip master synced: {Count} contracts for {Instruments} instruments", securities.Count, rows.Select(r => r.Underlying).Distinct().Count());
        return count;
    }

    /// <summary>Lot/tick/freeze and the spot security id come from the nearest future and the spot row.</summary>
    private async Task UpdateInstrumentsAsync(List<ScripRow> rows, Dictionary<string, Instrument> instruments, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var group in rows.GroupBy(r => r.Underlying))
        {
            var inst = await db.Instruments.SingleAsync(i => i.InstrumentId == instruments[group.Key].InstrumentId, ct);
            if (group.Where(r => r.Type is SecurityType.FutIdx or SecurityType.FutStk).MinBy(r => r.Expiry) is { } fut)
            {
                inst.LotSize = fut.LotSize;
                inst.TickSize = fut.TickSize;
                if (fut.FreezeQuantity is { } fq && fq >= fut.LotSize) inst.FreezeQuantity = fq;
                else if (inst.FreezeQuantity < inst.LotSize) inst.FreezeQuantity = inst.LotSize;
            }
            if (inst.UnderlyingSecurityId is null && group.FirstOrDefault(r => r.Type is SecurityType.Index or SecurityType.Equity) is { } spot)
                inst.UnderlyingSecurityId = spot.SecurityId;
        }
        await db.SaveChangesAsync(ct);
    }
}
