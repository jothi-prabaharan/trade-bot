using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.Repositories;

public sealed class MarketRepository(IDbContextFactory<TradeBotDbContext> factory) : RepositoryBase(factory), IMarketRepository
{
    public async Task<List<Instrument>> InstrumentsAsync(bool tradableOnly, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.Instruments.AsNoTracking().Where(i => i.IsActive && (!tradableOnly || i.IsTradable)).OrderBy(i => i.Symbol).ToListAsync(ct);
    }

    public async Task<List<SecurityMaster>> ContractsAsync(long instrumentId, DateOnly fromExpiry, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.SecurityMaster.AsNoTracking()
            .Where(s => s.InstrumentId == instrumentId && s.IsActive && (s.ExpiryDate == null || s.ExpiryDate >= fromExpiry))
            .ToListAsync(ct);
    }

    public async Task<SecurityMaster?> SecurityAsync(long securityId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.SecurityMaster.AsNoTracking().SingleOrDefaultAsync(s => s.SecurityId == securityId, ct);
    }

    public async Task<List<SecurityMaster>> SecuritiesAsync(IEnumerable<long> securityIds, CancellationToken ct)
    {
        var ids = securityIds.Distinct().ToList();
        await using var db = await Db(ct);
        return await db.SecurityMaster.AsNoTracking().Where(s => ids.Contains(s.SecurityId)).ToListAsync(ct);
    }

    public async Task<DateOnly?> LastScripSyncAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.SecurityMaster.MaxAsync(s => (DateOnly?)s.SyncedOn, ct);
    }

    public async Task<int> UpsertSecuritiesAsync(IReadOnlyCollection<SecurityMaster> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        await using var db = await Db(ct);
        const string sql = """
            INSERT INTO mst.security_master (security_id, instrument_id, exchange, segment, pml_symbol, security_type, strike_price, opt_type, expiry_date, lot_size, tick_size, is_active, synced_on)
            SELECT * FROM unnest(@id, @ins, @ex, @seg, @sym, @type, @strike, @opt, @exp, @lot, @tick, @active, @synced)
            ON CONFLICT (security_id) DO UPDATE SET
                instrument_id = EXCLUDED.instrument_id, exchange = EXCLUDED.exchange, segment = EXCLUDED.segment, pml_symbol = EXCLUDED.pml_symbol,
                security_type = EXCLUDED.security_type, strike_price = EXCLUDED.strike_price, opt_type = EXCLUDED.opt_type, expiry_date = EXCLUDED.expiry_date,
                lot_size = EXCLUDED.lot_size, tick_size = EXCLUDED.tick_size, is_active = EXCLUDED.is_active, synced_on = EXCLUDED.synced_on
            """;
        return await db.Database.ExecuteSqlRawAsync(sql, [
            Arr("id", rows.Select(r => r.SecurityId), NpgsqlDbType.Bigint),
            Arr("ins", rows.Select(r => r.InstrumentId), NpgsqlDbType.Bigint),
            Arr("ex", rows.Select(r => DbValue.ToDb(r.Exchange)), NpgsqlDbType.Text),
            Arr("seg", rows.Select(r => DbValue.ToDb(r.Segment)), NpgsqlDbType.Text),
            Arr("sym", rows.Select(r => r.PmlSymbol.Length > 100 ? r.PmlSymbol[..100] : r.PmlSymbol), NpgsqlDbType.Text),
            Arr("type", rows.Select(r => DbValue.ToDb(r.SecurityType)), NpgsqlDbType.Text),
            Arr("strike", rows.Select(r => r.StrikePrice), NpgsqlDbType.Numeric),
            Arr("opt", rows.Select(r => r.OptType is { } o ? DbValue.ToDb(o) : null), NpgsqlDbType.Text),
            Arr("exp", rows.Select(r => r.ExpiryDate), NpgsqlDbType.Date),
            Arr("lot", rows.Select(r => r.LotSize), NpgsqlDbType.Integer),
            Arr("tick", rows.Select(r => r.TickSize), NpgsqlDbType.Numeric),
            Arr("active", rows.Select(r => r.IsActive), NpgsqlDbType.Boolean),
            Arr("synced", rows.Select(r => r.SyncedOn), NpgsqlDbType.Date),
        ], ct);
    }

    public async Task<List<StockCompany>> CompaniesAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.StockCompanies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.NseSymbol).ToListAsync(ct);
    }

    public async Task SaveCandlesAsync(IReadOnlyCollection<ClosedBar> bars, CancellationToken ct)
    {
        if (bars.Count == 0) return;
        await using var db = await Db(ct);
        const string sql = """
            INSERT INTO trn.ohlcv_candles (instrument_id, security_id, timeframe, candle_time, open, high, low, close, volume, open_interest)
            SELECT * FROM unnest(@ins, @sec, @tf, @t, @o, @h, @l, @c, @v, @oi)
            ON CONFLICT (instrument_id, security_id, timeframe, candle_time) DO UPDATE SET
                open = EXCLUDED.open, high = EXCLUDED.high, low = EXCLUDED.low, close = EXCLUDED.close,
                volume = EXCLUDED.volume, open_interest = EXCLUDED.open_interest
            """;
        await db.Database.ExecuteSqlRawAsync(sql, [
            Arr("ins", bars.Select(b => b.Series.InstrumentId), NpgsqlDbType.Bigint),
            Arr("sec", bars.Select(b => b.Series.SecurityId), NpgsqlDbType.Bigint),
            Arr("tf", bars.Select(b => DbValue.ToDb(b.Timeframe)), NpgsqlDbType.Text),
            Arr("t", bars.Select(b => b.Bar.Time), NpgsqlDbType.TimestampTz),
            Arr("o", bars.Select(b => b.Bar.Open), NpgsqlDbType.Numeric),
            Arr("h", bars.Select(b => b.Bar.High), NpgsqlDbType.Numeric),
            Arr("l", bars.Select(b => b.Bar.Low), NpgsqlDbType.Numeric),
            Arr("c", bars.Select(b => b.Bar.Close), NpgsqlDbType.Numeric),
            Arr("v", bars.Select(b => b.Bar.Volume), NpgsqlDbType.Bigint),
            Arr("oi", bars.Select(b => b.Bar.OpenInterest), NpgsqlDbType.Bigint),
        ], ct);
    }

    public async Task<List<Bar>> RecentBarsAsync(SeriesKey series, Timeframe timeframe, DateTime fromUtc, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.OhlcvCandles.AsNoTracking()
            .Where(c => c.InstrumentId == series.InstrumentId && c.SecurityId == series.SecurityId && c.Timeframe == timeframe && c.CandleTime >= fromUtc)
            .OrderBy(c => c.CandleTime)
            .Select(c => new Bar(c.CandleTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.OpenInterest))
            .ToListAsync(ct);
    }

    public async Task<List<DailyWatchlist>> WatchlistAsync(DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyWatchlists.AsNoTracking().Include(w => w.Instrument).Where(w => w.TradingDate == date && w.IsActive).OrderByDescending(w => w.Score).ToListAsync(ct);
    }

    public async Task UpsertWatchlistAsync(IEnumerable<DailyWatchlist> rows, CancellationToken ct)
    {
        await using var db = await Db(ct);
        foreach (var r in rows)
        {
            var existing = await db.DailyWatchlists.SingleOrDefaultAsync(w => w.TradingDate == r.TradingDate && w.InstrumentId == r.InstrumentId, ct);
            if (existing is null) db.Add(r);
            else
            {
                existing.Score = Math.Max(existing.Score, r.Score);
                existing.Reason = r.Reason ?? existing.Reason;
                existing.IsActive = r.IsActive;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static NpgsqlParameter Arr<T>(string name, IEnumerable<T> values, NpgsqlDbType type) =>
        new(name, NpgsqlDbType.Array | type) { Value = values.ToArray() };
}

public sealed class IntelligenceRepository(IDbContextFactory<TradeBotDbContext> factory) : RepositoryBase(factory), IIntelligenceRepository
{
    public async Task<List<NewsImpact>> NewsImpactsAsync(DateOnly date, Instrument instrument, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var rows = await db.CompanyDailyNews.AsNoTracking().Where(n => n.TradingDate == date)
            .Select(n => new
            {
                n.NewsId, n.TradingDate, n.Sentiment, n.ImpactLevel, n.CompanyId, n.InstrumentId,
                CompanyInstrumentId = n.Company == null ? null : n.Company.InstrumentId,
                Nifty = n.Company == null ? 0 : n.Company.Nifty50WeightPct,
                BankNifty = n.Company == null ? 0 : n.Company.BankniftyWeightPct,
                FinNifty = n.Company == null ? 0 : n.Company.FinniftyWeightPct,
            }).ToListAsync(ct);

        return rows.Select(r => new NewsImpact(
            r.NewsId, r.TradingDate, r.Sentiment, r.ImpactLevel,
            IsMacro: r.CompanyId is null && r.InstrumentId is null,
            r.InstrumentId, r.CompanyInstrumentId,
            instrument.Symbol switch { "NIFTY" => r.Nifty, "BANKNIFTY" => r.BankNifty, "FINNIFTY" => r.FinNifty, _ => 0 }))
            .ToList();
    }

    public async Task<DailyMarketSentiment?> SentimentAsync(DateOnly date, long instrumentId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyMarketSentiments.AsNoTracking().SingleOrDefaultAsync(s => s.TradingDate == date && s.InstrumentId == instrumentId, ct);
    }

    public async Task<List<DailyMarketSentiment>> SentimentsAsync(DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyMarketSentiments.AsNoTracking().Include(s => s.Instrument).Where(s => s.TradingDate == date).ToListAsync(ct);
    }

    public async Task<CompanyDailyNews?> TopCatalystAsync(DateOnly date, long instrumentId, MarketBias bias, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var candidates = await db.CompanyDailyNews.AsNoTracking()
            .Where(n => n.TradingDate == date && n.Sentiment == bias && (n.InstrumentId == instrumentId || (n.Company != null && n.Company.InstrumentId == instrumentId)))
            .ToListAsync(ct);
        return candidates.OrderByDescending(n => n.ImpactLevel).ThenByDescending(n => Math.Abs(n.SentimentScore)).FirstOrDefault();
    }

    public async Task<List<CompanyDailyNews>> NewsAsync(DateOnly date, long? instrumentId, int limit, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var q = db.CompanyDailyNews.AsNoTracking().Include(n => n.Company).Where(n => n.TradingDate == date);
        if (instrumentId is { } id) q = q.Where(n => n.InstrumentId == id || (n.Company != null && n.Company.InstrumentId == id));
        return await q.OrderByDescending(n => n.AnalyzedAt).Take(limit).ToListAsync(ct);
    }

    public async Task<int> AddNewsAsync(IReadOnlyCollection<CompanyDailyNews> items, CancellationToken ct)
    {
        if (items.Count == 0) return 0;
        await using var db = await Db(ct);
        var dates = items.Select(i => i.TradingDate).Distinct().ToList();
        var existing = (await db.CompanyDailyNews.Where(n => dates.Contains(n.TradingDate)).Select(n => new { n.TradingDate, n.ContentHash }).ToListAsync(ct))
            .Select(x => (x.TradingDate, x.ContentHash.Trim())).ToHashSet();
        var fresh = items.Where(i => existing.Add((i.TradingDate, i.ContentHash))).ToList();
        db.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    public async Task UpsertSentimentAsync(DailyMarketSentiment sentiment, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var existing = await db.DailyMarketSentiments.SingleOrDefaultAsync(s => s.TradingDate == sentiment.TradingDate && s.InstrumentId == sentiment.InstrumentId, ct);
        if (existing is null) db.Add(sentiment);
        else
        {
            existing.NetSentimentScore = sentiment.NetSentimentScore;
            existing.DominantBias = sentiment.DominantBias;
            existing.ActiveCatalysts = sentiment.ActiveCatalysts;
            existing.TradingHaltActive = sentiment.TradingHaltActive;
            existing.HaltReason = sentiment.HaltReason;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<DetectedPattern> AddDetectionAsync(DetectedPattern detection, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(detection);
        await db.SaveChangesAsync(ct);
        return detection;
    }

    public async Task<List<DetectedPattern>> DetectionsAsync(DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var from = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - FixedLimits.IstOffset;
        return await db.DetectedPatterns.AsNoTracking().Include(d => d.Pattern).Include(d => d.Instrument)
            .Where(d => d.DetectedAt >= from && d.DetectedAt < from.AddDays(1)).OrderByDescending(d => d.DetectedAt).ToListAsync(ct);
    }

    public async Task<Signal> AddSignalAsync(Signal signal, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(signal);
        await db.SaveChangesAsync(ct);
        return signal;
    }

    public async Task UpdateSignalStatusAsync(long signalId, SignalStatus status, string? reason, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var s = await db.Signals.SingleAsync(x => x.SignalId == signalId, ct);
        s.Status = status;
        s.RejectionReason = reason is { Length: > 500 } ? reason[..500] : reason;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<Signal>> SignalsAsync(DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.Signals.AsNoTracking().Include(s => s.Security).Include(s => s.Detection).ThenInclude(d => d.Pattern)
            .Where(s => s.TradingDate == date).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
    }
}
