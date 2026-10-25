using Microsoft.EntityFrameworkCore;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.Repositories;

public sealed class AccountRepository(IDbContextFactory<TradeBotDbContext> factory) : RepositoryBase(factory), IAccountRepository
{
    public async Task<List<TradingAccount>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.TradingAccounts.AsNoTracking().Include(a => a.RiskConfig)
            .Where(a => !activeOnly || a.IsActive).OrderBy(a => a.AccountId).ToListAsync(ct);
    }

    public async Task<TradingAccount?> GetAsync(long accountId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.TradingAccounts.AsNoTracking().Include(a => a.RiskConfig).SingleOrDefaultAsync(a => a.AccountId == accountId, ct);
    }

    public async Task<TradingAccount> AddAsync(TradingAccount account, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task UpdateAsync(TradingAccount account, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, account, ct, account.AccountId);
    }

    public async Task UpdateRiskConfigAsync(AccountRiskConfig config, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, config, ct, config.ConfigId);
    }

    public async Task SetTradeModeAsync(long accountId, TradeMode mode, string changedBy, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var a = await db.TradingAccounts.SingleAsync(x => x.AccountId == accountId, ct);
        a.TradeMode = mode;
        if (mode == TradeMode.Real)
        {
            a.RealModeEnabledAt = nowUtc;
            a.RealModeEnabledBy = changedBy;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateFundsAsync(long accountId, decimal available, decimal utilised, decimal? opening, DateTime syncedUtc, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await db.AccountRiskConfigs.Where(r => r.AccountId == accountId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.AvailableMargin, available)
            .SetProperty(r => r.UtilisedMargin, utilised)
            .SetProperty(r => r.OpeningBalance, r => opening ?? r.OpeningBalance)
            .SetProperty(r => r.FundsSyncedAt, syncedUtc)
            .SetProperty(r => r.UpdatedAt, syncedUtc), ct);
    }

    public async Task<AccountAuthToken?> LatestTokenAsync(long accountId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.AccountAuthTokens.AsNoTracking().Where(t => t.AccountId == accountId)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.TokenId).FirstOrDefaultAsync(ct);
    }

    public async Task<Dictionary<long, AccountAuthToken>> LatestTokensAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        var ids = await db.AccountAuthTokens.GroupBy(t => t.AccountId).Select(g => g.Max(t => t.TokenId)).ToListAsync(ct);
        return await db.AccountAuthTokens.AsNoTracking().Where(t => ids.Contains(t.TokenId)).ToDictionaryAsync(t => t.AccountId, ct);
    }

    public async Task AddTokenAsync(AccountAuthToken token, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(token);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class ConfigRepository(IDbContextFactory<TradeBotDbContext> factory) : RepositoryBase(factory), IConfigRepository
{
    public async Task<StrategyConfig> StrategyAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        var active = await db.StrategyConfigs.AsNoTracking().SingleOrDefaultAsync(s => s.IsActive, ct);
        if (active is not null) return active;
        var created = new StrategyConfig();
        db.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task SaveStrategyAsync(StrategyConfig config, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, config, ct, config.StrategyConfigId);
    }

    public async Task<GeminiNewsConfig?> GeminiAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.GeminiNewsConfigs.AsNoTracking().SingleOrDefaultAsync(g => g.IsActive, ct);
    }

    public async Task SaveGeminiAsync(GeminiNewsConfig config, CancellationToken ct)
    {
        await using var db = await Db(ct);
        if (config.GeminiConfigId == 0)
        {
            db.Add(config);
            await db.SaveChangesAsync(ct);
        }
        else await UpdateScalarsAsync(db, config, ct, config.GeminiConfigId);
    }

    public async Task<List<ChargeRate>> ChargeRatesAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.ChargeRates.AsNoTracking().ToListAsync(ct);
    }

    public async Task<List<MarketHoliday>> HolidaysAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.MarketHolidays.AsNoTracking().OrderBy(h => h.HolidayDate).ToListAsync(ct);
    }

    public async Task<MarketHoliday> SaveHolidayAsync(MarketHoliday holiday, CancellationToken ct)
    {
        await using var db = await Db(ct);
        if (holiday.HolidayId == 0)
        {
            db.Add(holiday);
            await db.SaveChangesAsync(ct);
        }
        else await UpdateScalarsAsync(db, holiday, ct, holiday.HolidayId);
        return holiday;
    }

    public async Task DeleteHolidayAsync(long holidayId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await db.MarketHolidays.Where(h => h.HolidayId == holidayId).ExecuteDeleteAsync(ct);
    }

    public async Task<List<ChartPattern>> PatternsAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.ChartPatterns.AsNoTracking().OrderBy(p => p.Category).ThenBy(p => p.PatternCode).ToListAsync(ct);
    }

    public async Task<ChartPattern?> PatternAsync(long patternId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.ChartPatterns.AsNoTracking().SingleOrDefaultAsync(p => p.PatternId == patternId, ct);
    }

    public async Task UpdatePatternAsync(ChartPattern pattern, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, pattern, ct, pattern.PatternId);
    }

    public async Task<int> AddMissingPatternsAsync(IEnumerable<ChartPattern> patterns, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var existing = (await db.ChartPatterns.Select(p => p.PatternCode).ToListAsync(ct)).ToHashSet();
        var missing = patterns.Where(p => !existing.Contains(p.PatternCode)).ToList();
        db.AddRange(missing);
        await db.SaveChangesAsync(ct);
        return missing.Count;
    }

    public async Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.AlertChannels.AsNoTracking().ToListAsync(ct);
    }
}
