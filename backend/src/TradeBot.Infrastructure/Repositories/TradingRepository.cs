using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.Repositories;

public sealed class TradingRepository(IDbContextFactory<TradeBotDbContext> factory) : RepositoryBase(factory), ITradingRepository
{
    private static readonly OrderStatus[] Working = [OrderStatus.Pending, OrderStatus.Placed, OrderStatus.Open, OrderStatus.TriggerPending, OrderStatus.PartiallyFilled];

    public async Task<Order> AddOrderAsync(Order order, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(order);
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task UpdateOrderAsync(Order order, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, order, ct, order.OrderId);
    }

    public async Task<List<Order>> WorkingOrdersAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.Orders.AsNoTracking().Where(o => Working.Contains(o.Status)).ToListAsync(ct);
    }

    public async Task<bool> HasOrderForSignalAsync(long accountId, long signalId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.Orders.AnyAsync(o => o.AccountId == accountId && o.SignalId == signalId, ct);
    }

    public async Task<List<Order>> OrdersForEntryAsync(long entryOrderId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.Orders.AsNoTracking().Where(o => o.OrderId == entryOrderId || o.ParentOrderId == entryOrderId).OrderBy(o => o.OrderId).ToListAsync(ct);
    }

    public async Task<DailyTrade> AddTradeAsync(DailyTrade trade, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(trade);
        await db.SaveChangesAsync(ct);
        return trade;
    }

    public async Task UpdateTradeAsync(DailyTrade trade, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, trade, ct, trade.TradeId);
    }

    public async Task<DailyTrade?> TradeAsync(long tradeId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await WithDetails(db).SingleOrDefaultAsync(t => t.TradeId == tradeId, ct);
    }

    public async Task<List<DailyTrade>> OpenTradesAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await WithDetails(db).Where(t => t.TradeStatus == TradeStatus.Open).ToListAsync(ct);
    }

    public async Task<List<DailyTrade>> TradesAsync(DateOnly date, long? accountId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await WithDetails(db).Where(t => t.TradingDate == date && (accountId == null || t.AccountId == accountId))
            .OrderByDescending(t => t.EntryTime).ToListAsync(ct);
    }

    public async Task<int> TradesTodayAsync(long accountId, DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyTrades.CountAsync(t => t.AccountId == accountId && t.TradingDate == date, ct);
    }

    public async Task<int> OpenTradeCountAsync(long accountId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var trades = await db.DailyTrades.CountAsync(t => t.AccountId == accountId && t.TradeStatus == TradeStatus.Open, ct);
        // Entries still working at the broker count as open slots
        var entries = await db.Orders.CountAsync(o => o.AccountId == accountId && o.OrderLegType == OrderLegType.Entry && Working.Contains(o.Status), ct);
        return trades + entries;
    }

    public async Task<DateTime?> LastStopOutAsync(long accountId, long instrumentId, DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyTrades
            .Where(t => t.AccountId == accountId && t.TradingDate == date && t.Security.InstrumentId == instrumentId
                        && (t.TradeStatus == TradeStatus.SlHit || t.TradeStatus == TradeStatus.TrailingSlHit))
            .MaxAsync(t => t.ExitTime, ct);
    }

    public async Task<DailyAccountPnl> GetOrCreateDayPnlAsync(long accountId, DateOnly date, TradeMode mode, decimal openingBalance, decimal availableMargin, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var existing = await db.DailyAccountPnls.AsNoTracking().SingleOrDefaultAsync(p => p.AccountId == accountId && p.TradingDate == date, ct);
        if (existing is not null) return existing;
        var row = new DailyAccountPnl { AccountId = accountId, TradingDate = date, TradeMode = mode, OpeningBalance = openingBalance, AvailableMargin = availableMargin };
        db.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await using var retry = await Db(ct);
            return await retry.DailyAccountPnls.AsNoTracking().SingleAsync(p => p.AccountId == accountId && p.TradingDate == date, ct);
        }
        return row;
    }

    public async Task<DailyAccountPnl?> DayPnlAsync(long accountId, DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyAccountPnls.AsNoTracking().SingleOrDefaultAsync(p => p.AccountId == accountId && p.TradingDate == date, ct);
    }

    public async Task UpdateDayPnlAsync(DailyAccountPnl pnl, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, pnl, ct, pnl.SummaryId);
    }

    public async Task<List<DailyAccountPnl>> DayPnlsAsync(DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.DailyAccountPnls.AsNoTracking().Where(p => p.TradingDate == date).ToListAsync(ct);
    }

    public async Task<DailyBotState> GetOrCreateBotStateAsync(DateOnly date, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var existing = await db.DailyBotStates.AsNoTracking().SingleOrDefaultAsync(s => s.TradingDate == date, ct);
        if (existing is not null) return existing;
        var row = new DailyBotState { TradingDate = date };
        db.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await using var retry = await Db(ct);
            return await retry.DailyBotStates.AsNoTracking().SingleAsync(s => s.TradingDate == date, ct);
        }
        return row;
    }

    public async Task UpdateBotStateAsync(DailyBotState state, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await UpdateScalarsAsync(db, state, ct, state.TradingDate);
    }

    public async Task AddEventAsync(SystemEvent e, CancellationToken ct)
    {
        await using var db = await Db(ct);
        db.Add(e);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SystemEvent>> EventsAsync(int limit, CancellationToken ct)
    {
        await using var db = await Db(ct);
        return await db.SystemEvents.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync(ct);
    }

    private static IQueryable<DailyTrade> WithDetails(TradeBotDbContext db) =>
        db.DailyTrades.AsNoTracking()
            .Include(t => t.Security).ThenInclude(s => s.Instrument)
            .Include(t => t.Detection).ThenInclude(d => d.Pattern)
            .Include(t => t.News)
            .AsSplitQuery();
}
