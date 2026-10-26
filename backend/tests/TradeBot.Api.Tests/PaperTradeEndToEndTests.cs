using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Market;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Api.Tests;

/// <summary>
/// Full PAPER flow on the real host and database: API account → signal → dispatcher validation and sizing →
/// paper entry fill → mandatory SL-L + software target → ticks drive trailing, target, stop and manual exits.
/// </summary>
[Collection(ApiCollection.Name)]
public class PaperTradeEndToEndTests(ApiFactory api)
{
    private const long OptionSecurityId = 9_100_001;

    private MarketDataHub Hub => api.Get<MarketDataHub>();
    private PaperGateway Paper => api.Get<PaperGateway>();
    private TradeManager Trades => api.Get<TradeManager>();

    [Fact]
    public async Task Paper_account_trades_through_target_stop_cooldown_manual_exit_and_kill_switch()
    {
        var client = await api.AuthorizedClientAsync();
        var created = await client.PostAsJsonAsync("/api/accounts", new { accountName = "E2E Paper", clientId = "E2E-PAPER-1", userName = "E2E Trader" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var accountId = (await created.Content.ReadFromJsonAsync<JsonObject>())!["accountId"]!.GetValue<long>();

        var (instrument, option, pattern) = await SeedMarketAsync();
        await api.Get<ConfigCache>().ReloadAsync(CancellationToken.None);
        await api.Get<TokenStore>().ReloadAsync(CancellationToken.None);
        await Trades.StartAsync(CancellationToken.None);
        Hub.Register(new ScripInfo(option.SecurityId, instrument.InstrumentId, SeriesRole.Option, option));

        // ---- Trade 1: entry → trailing SL to breakeven → target ----
        Tick(100m);
        var outcome = await DispatchAsync(instrument, option, pattern, accountId);
        Assert.True(outcome.Placed, outcome.Message ?? string.Join(",", outcome.Violations.Select(v => v.Code)));
        Assert.Equal(2, outcome.Sizing!.Lots); // ₹2,000 risk budget / (₹15 × 65) per lot
        var t1 = await OpenTradeAsync(accountId);
        Assert.Equal((130, 100.25m, 85.45m, 130.65m), (t1.Trade.Quantity, t1.Trade.EntryBuyPrice, t1.Trade.StopLossPrice, t1.Trade.TargetSellPrice));
        Assert.Equal(OrderStatus.TriggerPending, t1.Sl!.Status);
        Assert.Equal(OrderStatus.Pending, t1.Target!.Status);

        Tick(110m);
        Tick(118m);
        await Eventually(() => t1.Trade.TrailingSlPrice > 85.45m, "trailing SL raised");
        Assert.Equal(100.25m, t1.Trade.TrailingSlPrice);
        await Eventually(async () => (await OrderAsync(t1.Sl.OrderId)).TriggerPrice == 100.25m, "SL-L order modified");

        Tick(131m);
        var closed1 = await ClosedTradeAsync(t1.Trade.TradeId);
        Assert.Equal(TradeStatus.TargetHit, closed1.TradeStatus);
        Assert.Equal(130.65m, closed1.ExitSellPrice);
        Assert.True(closed1.NetPnl > 0 && closed1.NetPnl < closed1.GrossPnl);

        // ---- Trade 2: stop-loss fills on the paper exchange ----
        Tick(100m);
        Assert.True((await DispatchAsync(instrument, option, pattern, accountId)).Placed);
        var t2 = await OpenTradeAsync(accountId);
        Tick(95m);
        Tick(84m);
        var closed2 = await ClosedTradeAsync(t2.Trade.TradeId);
        Assert.Equal(TradeStatus.SlHit, closed2.TradeStatus);
        Assert.True(closed2.ExitSellPrice is >= 83.7m and <= 85.45m);
        Assert.True(closed2.NetPnl < 0);

        // ---- Re-entry cooldown after the stop-out ----
        Tick(100m);
        var cooled = await DispatchAsync(instrument, option, pattern, accountId);
        Assert.False(cooled.Placed);
        Assert.Contains(cooled.Violations, v => v.Code == RuleCodes.ReentryCooldown);

        // ---- Trade 3 after the cooldown: dashboard shows it live, manual exit via the API ----
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        Tick(102m);
        Assert.True((await DispatchAsync(instrument, option, pattern, accountId)).Placed);
        var t3 = await OpenTradeAsync(accountId);
        Tick(108m);

        var overview = (await client.GetFromJsonAsync<JsonObject>($"/api/dashboard/overview?scope={accountId}"))!;
        var live = overview["trades"]!.AsArray().Single(r => r!["tradeId"]!.GetValue<long>() == t3.Trade.TradeId)!;
        Assert.Equal("OPEN", live["status"]!.GetValue<string>());
        Assert.Equal(108m, live["ltp"]!.GetValue<decimal>());
        Assert.True(live["progressPct"]!.GetValue<decimal>() > 0);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/trades/{t3.Trade.TradeId}/exit", null)).StatusCode);
        var closed3 = await ClosedTradeAsync(t3.Trade.TradeId);
        Assert.Equal(TradeStatus.ManualExit, closed3.TradeStatus);

        // ---- Ledger, dashboard and the mandatory SL/target on every order ----
        await using (var db = await api.DbAsync())
        {
            var orders = await db.Orders.AsNoTracking().Where(o => o.AccountId == accountId).ToListAsync();
            Assert.Equal(3, orders.Count(o => o.OrderLegType == OrderLegType.Entry));
            Assert.All(orders, o => Assert.True(o.StopLossPrice > 0 && o.TargetSellPrice > o.StopLossPrice, $"order {o.OrderId} lacks a valid SL/target"));
            Assert.DoesNotContain(orders, o => o.Status is OrderStatus.Placed or OrderStatus.Open or OrderStatus.TriggerPending);
            Assert.True(orders.Where(o => o.TxnType == TxnType.Sell && o.Status == OrderStatus.Filled).Sum(o => o.FilledQuantity)
                        == orders.Where(o => o.TxnType == TxnType.Buy && o.Status == OrderStatus.Filled).Sum(o => o.FilledQuantity), "position is flat");

            var day = await db.DailyAccountPnls.AsNoTracking().SingleAsync(d => d.AccountId == accountId && d.TradingDate == ApiFactory.TradingDay);
            Assert.Equal((3, closed1.NetPnl + closed2.NetPnl + closed3.NetPnl), (day.TotalTrades, day.NetPnl));
        }

        overview = (await client.GetFromJsonAsync<JsonObject>($"/api/dashboard/overview?scope={accountId}"))!;
        var pnl = overview["perAccount"]![0]!["pnl"]!;
        var acct = overview["accounts"]![0]!;
        Assert.Equal(acct["openingBalance"]!.GetValue<decimal>() + pnl["net"]!.GetValue<decimal>(), acct["balance"]!.GetValue<decimal>());
        Assert.Equal(pnl["net"]!.GetValue<decimal>(), acct["balanceChange"]!.GetValue<decimal>());
        Assert.Equal(3, pnl["totalTrades"]!.GetValue<int>());
        Assert.Equal(closed1.NetPnl + closed2.NetPnl + closed3.NetPnl, pnl["net"]!.GetValue<decimal>());
        Assert.Equal(new[] { "TARGET_HIT", "SL_HIT", "MANUAL_EXIT" },
            overview["trades"]!.AsArray().OrderBy(r => r!["tradeId"]!.GetValue<long>()).Select(r => r!["status"]!.GetValue<string>()));
        Assert.Contains(api.Alerts.Sent, a => a.Kind == AlertKind.Entry);
        Assert.Contains(api.Alerts.Sent, a => a.Kind == AlertKind.Exit);

        // ---- Chart data for the dashboard ----
        var curve = (await client.GetFromJsonAsync<JsonObject>($"/api/dashboard/pnl-curve?scope={accountId}"))!;
        var realized = curve["realized"]!.AsArray();
        Assert.Equal(4, realized.Count); // starts at 0, then one step per closed trade
        Assert.Equal(0m, realized[0]!["value"]!.GetValue<decimal>());
        Assert.Equal(closed1.NetPnl + closed2.NetPnl + closed3.NetPnl, realized[^1]!["value"]!.GetValue<decimal>());

        var bars = (await client.GetFromJsonAsync<JsonArray>($"/api/candles?instrumentId={instrument.InstrumentId}&securityId={option.SecurityId}&timeframe=1m&date={ApiFactory.TradingDay:yyyy-MM-dd}"))!;
        Assert.True(bars.Count >= 2, $"expected option candles, got {bars.Count}");
        Assert.Equal(131m, bars.Max(b => b!["high"]!.GetValue<decimal>()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/candles?instrumentId={instrument.InstrumentId}&timeframe=7m")).StatusCode);

        var instruments = (await client.GetFromJsonAsync<JsonArray>("/api/instruments"))!;
        Assert.Contains(instruments, i => i!["symbol"]!.GetValue<string>() == "NIFTY");

        var events = (await client.GetFromJsonAsync<JsonArray>("/api/events?afterId=0&limit=1000"))!.Select(e => e!["eventType"]!.GetValue<string>()).ToList();
        Assert.Contains("TRADE_OPENED", events);
        Assert.Contains("TRADE_CLOSED", events);
        Assert.Contains("SIGNAL_REJECTED", events);

        // ---- Global kill switch blocks new entries ----
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/bot/kill-switch", new { reason = "e2e" })).StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonObject>("/api/bot/status"))!["globalKillSwitch"]!.GetValue<bool>());
        Tick(103m);
        var killed = await DispatchAsync(instrument, option, pattern, accountId);
        Assert.Contains(killed.Violations, v => v.Code == RuleCodes.GlobalKillSwitch);
    }

    private void Tick(decimal ltp)
    {
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var t = new Tick(OptionSecurityId, ltp, api.Clock.GetUtcNow().UtcDateTime);
        Hub.OnTick(t);
        Paper.OnTick(t.SecurityId, t.Ltp, t.TimeUtc);
        api.Get<CandlePipeline>().OnTick(t);
        Trades.OnTick(t);
    }

    private async Task<(Instrument, SecurityMaster, ChartPattern)> SeedMarketAsync()
    {
        await using var db = await api.DbAsync();
        var instrument = new Instrument { Symbol = "NIFTY", InstrumentType = InstrumentType.Index, LotSize = 65, FreezeQuantity = 1800, TickSize = 0.05m, StrikeStep = 50, IsTradable = true };
        var pattern = new ChartPattern { PatternCode = "BULLISH_ENGULFING_E2E", PatternName = "Bullish Engulfing", Category = PatternCategory.Candlestick, Direction = PatternDirection.Bullish, MinCandlesRequired = 2, DefaultWeight = 60 };
        db.AddRange(instrument, pattern);
        await db.SaveChangesAsync();
        var option = new SecurityMaster
        {
            SecurityId = OptionSecurityId, InstrumentId = instrument.InstrumentId, PmlSymbol = "NIFTY 25000 CE", SecurityType = SecurityType.OptIdx,
            StrikePrice = 25000, OptType = OptionType.Ce, ExpiryDate = ApiFactory.TradingDay.AddDays(2), LotSize = 65, TickSize = 0.05m, SyncedOn = ApiFactory.TradingDay,
        };
        db.Add(option);
        await db.SaveChangesAsync();
        return (instrument, option, pattern);
    }

    private async Task<DispatchOutcome> DispatchAsync(Instrument instrument, SecurityMaster option, ChartPattern pattern, long accountId)
    {
        await using var db = await api.DbAsync();
        var detection = new DetectedPattern
        {
            InstrumentId = instrument.InstrumentId, PatternId = pattern.PatternId, Timeframe = Timeframe.M5, TrendTimeframe = Timeframe.M15, TrendBias = MarketBias.Bullish,
            Direction = PatternDirection.Bullish, NeckOrBreakoutPrice = 25010, StructuralSlPrice = 24960, StructuralTargetPrice = 25110, ConfidenceScore = 78, IsConfirmed = true,
        };
        db.Add(detection);
        await db.SaveChangesAsync();
        var ltp = Hub.Last(option.SecurityId)!.Value.Ltp;
        var signal = new Signal
        {
            TradingDate = ApiFactory.TradingDay, DetectionId = detection.DetectionId, InstrumentId = instrument.InstrumentId, SecurityId = option.SecurityId,
            SpotPrice = 25010, OptionDelta = 0.52m, EntryOptionLtp = ltp, MandatorySlPrice = ltp * 0.85m, MandatoryTargetPrice = ltp * 1.3m,
            SlMethod = SlMethod.FixedPctFallback, PatternScore = 78, NewsScore = 65, OiGreeksScore = 60, CombinedAiConfidence = 72,
        };
        db.Add(signal);
        await db.SaveChangesAsync();

        var outcomes = await api.Get<Dispatcher>().DispatchAsync(new SignalContext(signal, detection, pattern, instrument, option, null), CancellationToken.None);
        return outcomes.Single(o => o.AccountId == accountId);
    }

    private async Task<LiveTrade> OpenTradeAsync(long accountId)
    {
        LiveTrade? live = null;
        await Eventually(() => (live = Trades.OpenTrades.SingleOrDefault(t => t.Account.AccountId == accountId)) is not null, "trade opened");
        return live!;
    }

    private async Task<DailyTrade> ClosedTradeAsync(long tradeId)
    {
        DailyTrade? t = null;
        await Eventually(async () =>
        {
            await using var db = await api.DbAsync();
            t = await db.DailyTrades.AsNoTracking().SingleAsync(x => x.TradeId == tradeId);
            return t.TradeStatus != TradeStatus.Open;
        }, $"trade {tradeId} closed");
        await Eventually(() => Trades.OpenTrades.All(x => x.Trade.TradeId != tradeId), "trade released");
        return t!;
    }

    private async Task<Order> OrderAsync(long orderId)
    {
        await using var db = await api.DbAsync();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);
    }

    private static Task Eventually(Func<bool> condition, string what) => Eventually(() => Task.FromResult(condition()), what);

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }
}
