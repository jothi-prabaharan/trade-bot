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
        var parsed = SecurityMasterCsv.Parse(csv, options.Value.StrikeDivisor).Where(r => r.Exchange == "NSE").ToList();
        await ReconcileStockUniverseAsync(parsed, today, ct);
        var instruments = (await market.InstrumentsAsync(tradableOnly: false, ct)).ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        var horizon = today.AddDays(HorizonDays);

        var rows = parsed
            .Where(r => instruments.ContainsKey(r.Underlying))
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

    /// <summary>A scrip master listing fewer stock underlyings than this is treated as malformed and not used to change the universe.</summary>
    public const int MinStockUnderlyings = 50;

    /// <summary>
    /// The F&amp;O stock universe follows the scrip master: stocks with live futures/options are added (weights 0 until the
    /// seed CSV lists them), stocks without any are flagged not-F&amp;O and untradable, and returning stocks are re-enabled.
    /// </summary>
    internal async Task<(int Added, int Removed, int Restored)> ReconcileStockUniverseAsync(IReadOnlyList<ScripRow> rows, DateOnly today, CancellationToken ct)
    {
        var fno = rows.Where(r => r.Type is SecurityType.FutStk or SecurityType.OptStk && r.Expiry >= today)
            .GroupBy(r => r.Underlying.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Type == SecurityType.FutStk ? 0 : 1).ThenBy(r => r.Expiry).First());
        if (fno.Count < MinStockUnderlyings)
        {
            log.LogWarning("Scrip master lists only {Count} F&O stock underlyings; universe left unchanged", fno.Count);
            return (0, 0, 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.Instruments.ToListAsync(ct);
        var stocks = all.Where(i => i.InstrumentType == InstrumentType.Stock).ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        var taken = all.Select(i => i.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (symbol, spec) in fno.Where(x => !taken.Contains(x.Key)))
        {
            var i = new Instrument
            {
                Symbol = symbol, Name = symbol, InstrumentType = InstrumentType.Stock, LotSize = spec.LotSize, TickSize = spec.TickSize,
                FreezeQuantity = spec.FreezeQuantity is { } fq && fq >= spec.LotSize ? fq : spec.LotSize, IsTradable = true,
            };
            db.Add(i);
            stocks[symbol] = i;
            added++;
        }
        await db.SaveChangesAsync(ct);

        var companies = await db.StockCompanies.ToDictionaryAsync(c => c.NseSymbol, StringComparer.OrdinalIgnoreCase, ct);
        int removed = 0, restored = 0;
        foreach (var i in stocks.Values)
        {
            var live = fno.ContainsKey(i.Symbol);
            if (!companies.TryGetValue(i.Symbol, out var c))
            {
                db.Add(new StockCompany { NseSymbol = i.Symbol, CompanyName = i.Name ?? i.Symbol, InstrumentId = i.InstrumentId, IsFnoActive = live });
                continue;
            }
            if (!live && c.IsFnoActive)
            {
                c.IsFnoActive = false;
                i.IsTradable = false;
                removed++;
            }
            else if (live && !c.IsFnoActive)
            {
                c.IsFnoActive = true;
                i.IsTradable = true;
                restored++;
            }
        }
        await db.SaveChangesAsync(ct);
        if (added + removed + restored > 0)
            log.LogInformation("F&O universe: {Added} added, {Removed} left the segment, {Restored} returned", added, removed, restored);
        return (added, removed, restored);
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
