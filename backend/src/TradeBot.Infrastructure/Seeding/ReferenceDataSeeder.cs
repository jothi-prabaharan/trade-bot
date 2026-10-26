using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Patterns;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.Seeding;

public sealed record SeedReport(int IndicesAdded, int StocksAdded, int CompaniesUpdated, int WeightsCleared, int PatternsAdded, int HolidaysUpserted, int ChargeRatesAdded);

public sealed record StockSeedRow(string Symbol, string CompanyName, string? Sector, decimal Nifty50WeightPct, decimal BankniftyWeightPct, decimal FinniftyWeightPct);

public sealed record HolidaySeedRow(DateOnly Date, Exchange Exchange, string Description);

/// <summary>
/// Idempotent reference data for a fresh or existing database: indices, the F&amp;O stock universe with index weights,
/// all chart-pattern detectors, default strategy/Gemini rows, option charge rates and NSE holidays. Re-running it
/// refreshes names and weights but keeps what the dashboard or the scrip-master sync manage (pattern toggles,
/// lot/freeze sizes, F&amp;O status).
/// </summary>
public sealed class ReferenceDataSeeder(IDbContextFactory<TradeBotDbContext> factory, IConfigRepository configs, ILogger<ReferenceDataSeeder> log)
{
    /// <summary>Lot/freeze sizes as of the January 2026 revision; the daily scrip-master sync keeps them current.</summary>
    public static readonly IReadOnlyList<(string Symbol, string Name, int Lot, int Freeze, decimal StrikeStep, bool Weekly, bool Tradable)> Indices =
    [
        ("NIFTY", "Nifty 50", 65, 1800, 50, true, true),
        ("BANKNIFTY", "Nifty Bank", 30, 900, 100, false, true),
        ("FINNIFTY", "Nifty Financial Services", 60, 1800, 50, false, false),
    ];

    public static readonly DateOnly ChargeRatesEffectiveFrom = new(2024, 10, 1);

    public async Task<SeedReport> SeedAsync(IReadOnlyList<StockSeedRow> stocks, IReadOnlyList<HolidaySeedRow> holidays, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var instruments = await db.Instruments.ToDictionaryAsync(i => i.Symbol, StringComparer.OrdinalIgnoreCase, ct);
        var indicesAdded = 0;
        foreach (var (symbol, name, lot, freeze, step, weekly, tradable) in Indices.Where(x => !instruments.ContainsKey(x.Symbol)))
        {
            var i = new Instrument
            {
                Symbol = symbol, Name = name, InstrumentType = InstrumentType.Index, LotSize = lot, FreezeQuantity = freeze, TickSize = 0.05m,
                StrikeStep = step, HasWeeklyExpiry = weekly, IsTradable = tradable,
            };
            db.Add(i);
            instruments[symbol] = i;
            indicesAdded++;
        }

        var stocksAdded = 0;
        foreach (var s in stocks.Where(s => !instruments.ContainsKey(s.Symbol)))
        {
            // Placeholder lot/freeze until the scrip-master sync reads the real contract specs
            var i = new Instrument { Symbol = s.Symbol, Name = s.CompanyName, InstrumentType = InstrumentType.Stock, LotSize = 1, FreezeQuantity = 1, TickSize = 0.05m, IsTradable = true };
            db.Add(i);
            instruments[s.Symbol] = i;
            stocksAdded++;
        }
        await db.SaveChangesAsync(ct);

        var companies = await db.StockCompanies.ToDictionaryAsync(c => c.NseSymbol, StringComparer.OrdinalIgnoreCase, ct);
        var seeded = stocks.Select(s => s.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        foreach (var s in stocks)
        {
            if (!companies.TryGetValue(s.Symbol, out var c))
            {
                c = new StockCompany { NseSymbol = s.Symbol, CompanyName = s.CompanyName, IsFnoActive = true };
                db.Add(c);
            }
            c.CompanyName = s.CompanyName;
            c.Sector = s.Sector ?? c.Sector;
            c.InstrumentId = instruments[s.Symbol].InstrumentId;
            c.Nifty50WeightPct = s.Nifty50WeightPct;
            c.BankniftyWeightPct = s.BankniftyWeightPct;
            c.FinniftyWeightPct = s.FinniftyWeightPct;
            updated++;
        }
        // The file is the source of truth for index weights: companies it no longer lists drop out of the indices
        var cleared = 0;
        foreach (var c in companies.Values.Where(c => !seeded.Contains(c.NseSymbol) && (c.Nifty50WeightPct + c.BankniftyWeightPct + c.FinniftyWeightPct) > 0))
        {
            c.Nifty50WeightPct = c.BankniftyWeightPct = c.FinniftyWeightPct = 0;
            cleared++;
        }

        var existingHolidays = await db.MarketHolidays.ToDictionaryAsync(h => (h.Exchange, h.HolidayDate), ct);
        foreach (var h in holidays)
        {
            if (existingHolidays.TryGetValue((h.Exchange, h.Date), out var row)) row.Description = h.Description;
            else db.Add(new MarketHoliday { HolidayDate = h.Date, Exchange = h.Exchange, Description = h.Description });
        }

        var rateTypes = await db.ChargeRates.Select(r => r.SecurityType).Distinct().ToListAsync(ct);
        var ratesAdded = 0;
        foreach (var type in new[] { SecurityType.OptIdx, SecurityType.OptStk }.Except(rateTypes))
        {
            var d = ChargesCalculator.DefaultOptionRates;
            db.Add(new ChargeRate
            {
                EffectiveFrom = ChargeRatesEffectiveFrom, SecurityType = type, BrokeragePerOrder = d.BrokeragePerOrder, SttSellPct = d.SttSellPct,
                ExchangeTxnPct = d.ExchangeTxnPct, SebiFeePct = d.SebiFeePct, StampDutyBuyPct = d.StampDutyBuyPct, GstPct = d.GstPct,
            });
            ratesAdded++;
        }

        if (!await db.GeminiNewsConfigs.AnyAsync(g => g.IsActive, ct)) db.Add(new GeminiNewsConfig { ModelName = "gemini-flash-latest" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await configs.StrategyAsync(ct); // creates the default row when missing
        var patterns = await configs.AddMissingPatternsAsync(PatternRegistry.All.Select(d => PatternRegistry.ToCatalogRow(d.Definition)), ct);

        var report = new SeedReport(indicesAdded, stocksAdded, updated, cleared, patterns, holidays.Count, ratesAdded);
        log.LogInformation("Reference data seeded: {Report}", report);
        return report;
    }

    /// <summary>symbol,company_name,sector,nifty50_weight_pct,banknifty_weight_pct,finnifty_weight_pct ('#' lines are comments).</summary>
    public static List<StockSeedRow> ParseStocks(string csv)
    {
        var rows = new List<StockSeedRow>();
        foreach (var (cols, line) in CsvRows(csv, "symbol"))
        {
            if (cols.Count < 6) throw new FormatException($"Line {line}: expected 6 columns.");
            var symbol = cols[0].Trim().ToUpperInvariant();
            if (symbol.Length is 0 or > 30 || cols[1].Trim().Length == 0) throw new FormatException($"Line {line}: symbol and company name are required.");
            var weights = cols.Skip(3).Take(3).Select(w => decimal.Parse(w, NumberStyles.Number, CultureInfo.InvariantCulture)).ToArray();
            if (weights.Any(w => w is < 0 or > 100)) throw new FormatException($"Line {line}: weights must be between 0 and 100.");
            rows.Add(new StockSeedRow(symbol, cols[1].Trim(), cols[2].Trim() is { Length: > 0 } s ? s : null, weights[0], weights[1], weights[2]));
        }
        var dup = rows.GroupBy(r => r.Symbol).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new FormatException($"Duplicate symbol {dup.Key}.");
        foreach (var (name, total) in new[] { ("NIFTY 50", rows.Sum(r => r.Nifty50WeightPct)), ("BANK NIFTY", rows.Sum(r => r.BankniftyWeightPct)), ("FIN NIFTY", rows.Sum(r => r.FinniftyWeightPct)) })
            if (total != 0 && Math.Abs(total - 100) > 0.5m) throw new FormatException($"{name} weights sum to {total}, expected 100.");
        return rows;
    }

    /// <summary>date,exchange,description ('#' lines are comments).</summary>
    public static List<HolidaySeedRow> ParseHolidays(string csv) =>
        CsvRows(csv, "date").Select(x => new HolidaySeedRow(
            DateOnly.ParseExact(x.Cols[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DbValue.FromDb<Exchange>(x.Cols[1].Trim().ToUpperInvariant()),
            x.Cols[2].Trim())).ToList();

    private static IEnumerable<(List<string> Cols, int Line)> CsvRows(string csv, string headerFirstColumn)
    {
        var line = 0;
        foreach (var raw in csv.Split('\n'))
        {
            line++;
            var text = raw.TrimEnd('\r');
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(headerFirstColumn + ",", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (SplitCsv(text), line);
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var cols = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cols.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cols.Add(cur.ToString());
        return cols;
    }
}
