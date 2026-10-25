using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SchemaConstraintTests(PostgresFixture fx)
{
    private static async Task AssertViolatesAsync(Func<Task> act, string constraint)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(act);
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(constraint, pg.ConstraintName);
    }

    [Theory]
    [InlineData(100, 100, 130)] // SL == entry
    [InlineData(100, 110, 130)] // SL above entry
    [InlineData(100, 85, 100)]  // target == entry
    [InlineData(100, 0, 130)]   // SL zero
    public async Task Signal_rejects_invalid_mandatory_sl_target(decimal entry, decimal sl, decimal target)
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        db.Add(PostgresFixture.NewSignal(g.DetectionId, g.InstrumentId, g.SecurityId, entry, sl, target));
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_signals_mandatory_sl_target");
    }

    [Fact]
    public async Task Signal_risk_reward_is_computed_by_database()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        var signal = await db.Signals.SingleAsync(s => s.SignalId == g.SignalId);
        Assert.Equal(2.0m, signal.RiskRewardRatio);
    }

    [Fact]
    public async Task Entry_order_price_must_sit_inside_bracket()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        db.Add(PostgresFixture.NewEntryOrder(g, price: 131));
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_orders_entry_bracket");
    }

    [Fact]
    public async Task Exit_leg_rejects_target_below_stop_loss()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        var entry = PostgresFixture.NewEntryOrder(g);
        db.Add(entry);
        await db.SaveChangesAsync();

        db.Add(new Order
        {
            AccountId = g.AccountId, SignalId = g.SignalId, ParentOrderId = entry.OrderId, OrderLegType = OrderLegType.TargetSell, TradeMode = TradeMode.Paper,
            TxnType = TxnType.Sell, SecurityId = g.SecurityId, Quantity = 65, Price = 80, StopLossPrice = 90, TargetSellPrice = 80,
            OrderLtp = 100, OrderType = OrderType.Limit
        });
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_orders_mandatory_sl_target");
    }

    [Fact]
    public async Task Exit_leg_requires_parent_entry_order()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        var orphan = PostgresFixture.NewEntryOrder(g);
        orphan.OrderLegType = OrderLegType.TargetSell;
        orphan.TxnType = TxnType.Sell;
        orphan.Price = 130;
        db.Add(orphan);
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_orders_parent_link");
    }

    [Fact]
    public async Task Stop_loss_leg_requires_trigger_price()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        var entry = PostgresFixture.NewEntryOrder(g);
        db.Add(entry);
        await db.SaveChangesAsync();

        db.Add(new Order
        {
            AccountId = g.AccountId, SignalId = g.SignalId, ParentOrderId = entry.OrderId, OrderLegType = OrderLegType.StopLoss, TradeMode = TradeMode.Paper,
            TxnType = TxnType.Sell, SecurityId = g.SecurityId, Quantity = 65, Price = 84.5m, TriggerPrice = null,
            StopLossPrice = 85, TargetSellPrice = 130, OrderLtp = 100, OrderType = OrderType.StopLossLimit
        });
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_orders_sl_trigger");
    }

    [Fact]
    public async Task Full_bracket_and_trade_round_trip()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        var entry = PostgresFixture.NewEntryOrder(g);
        entry.Status = OrderStatus.Filled;
        entry.FilledQuantity = 65;
        entry.AvgFillPrice = 100.25m;
        db.Add(entry);
        await db.SaveChangesAsync();

        var sl = new Order
        {
            AccountId = g.AccountId, SignalId = g.SignalId, ParentOrderId = entry.OrderId, OrderLegType = OrderLegType.StopLoss, TradeMode = TradeMode.Paper,
            TxnType = TxnType.Sell, SecurityId = g.SecurityId, Quantity = 65, Price = 84.5m, TriggerPrice = 85,
            StopLossPrice = 85, TargetSellPrice = 130, OrderLtp = 100.25m, OrderType = OrderType.StopLossLimit, Status = OrderStatus.TriggerPending
        };
        var target = new Order
        {
            AccountId = g.AccountId, SignalId = g.SignalId, ParentOrderId = entry.OrderId, OrderLegType = OrderLegType.TargetSell, TradeMode = TradeMode.Paper,
            TxnType = TxnType.Sell, SecurityId = g.SecurityId, Quantity = 65, Price = 130, StopLossPrice = 85, TargetSellPrice = 130,
            OrderLtp = 100.25m, OrderType = OrderType.Limit
        };
        db.AddRange(sl, target);
        await db.SaveChangesAsync();

        var trade = new DailyTrade
        {
            AccountId = g.AccountId, TradeMode = TradeMode.Paper, SignalId = g.SignalId, SecurityId = g.SecurityId, DetectionId = g.DetectionId,
            EntryOrderId = entry.OrderId, SlOrderId = sl.OrderId, TargetOrderId = target.OrderId, Quantity = 65, Lots = 1,
            EntryBuyPrice = 100.25m, StopLossPrice = 85, TrailingSlPrice = 85, TargetSellPrice = 130, GrossPnl = 650, BrokerageAndCharges = 52.4m,
            EntryTime = DateTime.UtcNow
        };
        db.Add(trade);
        await db.SaveChangesAsync();

        await using var read = fx.CreateContext();
        var loaded = await read.DailyTrades.Include(t => t.EntryOrder).ThenInclude(o => o.ChildOrders).SingleAsync(t => t.TradeId == trade.TradeId);
        Assert.Equal(597.6m, loaded.NetPnl);
        Assert.Equal(2, loaded.EntryOrder.ChildOrders.Count);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Kolkata")), loaded.TradingDate);
    }

    [Fact]
    public async Task Trailing_sl_cannot_drop_below_initial_sl()
    {
        var g = await fx.SeedAsync();
        await using var db = fx.CreateContext();
        var entry = PostgresFixture.NewEntryOrder(g);
        db.Add(entry);
        await db.SaveChangesAsync();

        db.Add(new DailyTrade
        {
            AccountId = g.AccountId, TradeMode = TradeMode.Paper, SignalId = g.SignalId, SecurityId = g.SecurityId, DetectionId = g.DetectionId,
            EntryOrderId = entry.OrderId, Quantity = 65, Lots = 1, EntryBuyPrice = 100, StopLossPrice = 85, TrailingSlPrice = 80,
            TargetSellPrice = 130, EntryTime = DateTime.UtcNow
        });
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_daily_trades_trailing_sl");
    }

    [Fact]
    public async Task Concurrent_trade_updates_are_detected_via_xmin()
    {
        var g = await fx.SeedAsync();
        long tradeId;
        await using (var db = fx.CreateContext())
        {
            var entry = PostgresFixture.NewEntryOrder(g);
            db.Add(entry);
            await db.SaveChangesAsync();
            var trade = new DailyTrade
            {
                AccountId = g.AccountId, TradeMode = TradeMode.Paper, SignalId = g.SignalId, SecurityId = g.SecurityId, DetectionId = g.DetectionId,
                EntryOrderId = entry.OrderId, Quantity = 65, Lots = 1, EntryBuyPrice = 100, StopLossPrice = 85, TrailingSlPrice = 85,
                TargetSellPrice = 130, EntryTime = DateTime.UtcNow
            };
            db.Add(trade);
            await db.SaveChangesAsync();
            tradeId = trade.TradeId;
        }

        await using var a = fx.CreateContext();
        await using var b = fx.CreateContext();
        var ta = await a.DailyTrades.SingleAsync(t => t.TradeId == tradeId);
        var tb = await b.DailyTrades.SingleAsync(t => t.TradeId == tradeId);

        ta.TrailingSlPrice = 100;
        await a.SaveChangesAsync();
        tb.TradeStatus = TradeStatus.SlHit;
        tb.ExitSellPrice = 85;
        tb.ExitTime = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }

    [Fact]
    public async Task Risk_config_fixed_fallback_must_meet_min_rr()
    {
        await using var db = fx.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Add(new TradingAccount
        {
            AccountName = suffix, ClientId = suffix, UserName = "u",
            RiskConfig = new AccountRiskConfig { DefaultSlPct = 15, DefaultTargetPct = 20, MinRiskRewardRatio = 2 }
        });
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_account_risk_config_fallback_rr");
    }

    [Fact]
    public async Task Real_mode_requires_explicit_confirmation()
    {
        await using var db = fx.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Add(new TradingAccount { AccountName = suffix, ClientId = suffix, UserName = "u", TradeMode = TradeMode.Real });
        await AssertViolatesAsync(() => db.SaveChangesAsync(), "ck_trading_accounts_real_mode_confirmed");
    }

    [Fact]
    public async Task Secrets_are_encrypted_at_rest_and_enums_stored_as_text()
    {
        await using var db = fx.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var account = new TradingAccount { AccountName = suffix, ClientId = suffix, UserName = "u", ApiKey = "plain-api-key", TotpSecret = "JBSWY3DPEHPK3PXP" };
        db.Add(account);
        await db.SaveChangesAsync();

        var raw = await db.Database
            .SqlQuery<RawAccountRow>($"SELECT api_key, totp_secret, trade_mode FROM mst.trading_accounts WHERE account_id = {account.AccountId}")
            .SingleAsync();
        Assert.StartsWith("v1:", raw.ApiKey);
        Assert.DoesNotContain("plain-api-key", raw.ApiKey);
        Assert.Equal("PAPER", raw.TradeMode);

        await using var read = fx.CreateContext();
        var loaded = await read.TradingAccounts.SingleAsync(a => a.AccountId == account.AccountId);
        Assert.Equal("plain-api-key", loaded.ApiKey);
        Assert.Equal("JBSWY3DPEHPK3PXP", loaded.TotpSecret);
    }

    private sealed record RawAccountRow(string ApiKey, string TotpSecret, string TradeMode);
}
