using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Patterns;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

/// <summary>Loads trigger/trend timeframe history for underlyings: DB first, then the Paytm price-chart API.</summary>
public sealed class HistoryBackfill(ConfigCache config, IPaytmMoneyClient client, TokenStore tokens, IMarketRepository market, BarHistory history,
    TimeProvider clock, ILogger<HistoryBackfill> log)
{
    private static int Needed(Timeframe tf) => tf switch { Timeframe.M15 => 60, Timeframe.H1 => 30, _ => 150 };

    public async Task RunAsync(IEnumerable<Instrument> instruments, CancellationToken ct)
    {
        var today = Ist.Today(clock);
        var from = today.AddDays(-(int)Math.Ceiling(config.Strategy.HistoryBackfillDays * 1.6) - 2);
        var fromUtc = Ist.ToUtc(from, TimeOnly.MinValue);
        var provider = config.Accounts.FirstOrDefault(a => a.IsDataProvider) ?? config.Accounts.FirstOrDefault();
        var token = provider is null ? null : tokens.ReadToken(provider.AccountId);

        foreach (var inst in instruments)
            foreach (var tf in new[] { config.Strategy.TriggerTimeframe, config.Strategy.TrendTimeframe }.Distinct())
            {
                var series = new SeriesKey(inst.InstrumentId, null);
                var bars = await market.RecentBarsAsync(series, tf, fromUtc, ct);
                if (bars.Count < Needed(tf) && token is not null)
                {
                    try
                    {
                        var type = inst.InstrumentType == InstrumentType.Index ? SecurityType.FutIdx : SecurityType.FutStk;
                        var fetched = await client.GetPriceChartAsync(token, new PriceChartRequest(inst.Symbol, type, tf, from, today), ct);
                        var closedOnly = fetched.Where(b => Timeframes.BucketStart(b.Time, tf) is null || b.Time + Timeframes.Span(tf) <= clock.GetUtcNow().UtcDateTime).ToList();
                        await market.SaveCandlesAsync(closedOnly.Select(b => new ClosedBar(series, tf, b)).ToList(), ct);
                        bars = await market.RecentBarsAsync(series, tf, fromUtc, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogWarning(e, "Backfill {Symbol} {Tf} failed; patterns warm up from live ticks", inst.Symbol, tf);
                    }
                }
                history.Load(series, tf, bars);
                log.LogInformation("History {Symbol} {Tf}: {Count} bars", inst.Symbol, DbValue.ToDb(tf), bars.Count);
            }
    }
}

/// <summary>Daily stock watchlist: Gemini catalyst strength (60%) + previous-session futures liquidity (40%).</summary>
public sealed class WatchlistBuilder(ConfigCache config, IMarketRepository market, IIntelligenceRepository intel, TimeProvider clock, ILogger<WatchlistBuilder> log)
{
    public async Task<List<long>> BuildAsync(CancellationToken ct)
    {
        var today = Ist.Today(clock);
        var rows = new List<DailyWatchlist>();
        foreach (var idx in config.Instruments.Where(i => i.IsTradable && i.InstrumentType == InstrumentType.Index))
            rows.Add(new DailyWatchlist { TradingDate = today, InstrumentId = idx.InstrumentId, Source = WatchlistSource.Index, Score = 100, Reason = "Index" });

        var size = config.Strategy.StockWatchlistSize;
        var stocks = config.Instruments.Where(i => i.IsTradable && i.InstrumentType == InstrumentType.Stock).ToList();
        if (size > 0 && stocks.Count > 0)
        {
            var sentiment = (await intel.SentimentsAsync(today, ct)).ToDictionary(s => s.InstrumentId);
            var since = Ist.ToUtc(today.AddDays(-5), TimeOnly.MinValue);
            var liquidity = new Dictionary<long, long>();
            foreach (var s in stocks)
            {
                var bars = await market.RecentBarsAsync(new SeriesKey(s.InstrumentId, null), Timeframe.M15, since, ct);
                var lastDay = bars.Count == 0 ? (DateOnly?)null : bars.Max(b => DateOnly.FromDateTime(Validation.FixedLimits.ToIst(b.Time)));
                liquidity[s.InstrumentId] = bars.Where(b => DateOnly.FromDateTime(Validation.FixedLimits.ToIst(b.Time)) == lastDay).Sum(b => b.Volume);
            }
            var ranked = liquidity.OrderBy(x => x.Value).Select((x, i) => (x.Key, Pct: liquidity.Count <= 1 ? 100m : 100m * i / (liquidity.Count - 1))).ToDictionary(x => x.Key, x => x.Pct);

            var scored = stocks.Select(s =>
            {
                var news = sentiment.TryGetValue(s.InstrumentId, out var st) ? Math.Abs(st.NetSentimentScore) : 0;
                var liq = liquidity[s.InstrumentId] > 0 ? ranked[s.InstrumentId] : 0;
                return (Stock: s, Score: Math.Round(0.6m * news + 0.4m * liq, 2), News: news, Halted: st?.TradingHaltActive == true);
            })
            .Where(x => x.Score > 0 && !x.Halted)
            .OrderByDescending(x => x.Score).Take(size);

            rows.AddRange(scored.Select(x => new DailyWatchlist
            {
                TradingDate = today, InstrumentId = x.Stock.InstrumentId, Source = x.News > 0 ? WatchlistSource.News : WatchlistSource.Premarket,
                Score = x.Score, Reason = x.News > 0 ? $"Catalyst |{x.News:0}|" : "Liquidity",
            }));
        }
        await market.UpsertWatchlistAsync(rows, ct);
        log.LogInformation("Watchlist: {Indices} indices, {Stocks} stocks", rows.Count(r => r.Source == WatchlistSource.Index), rows.Count(r => r.Source != WatchlistSource.Index));
        return rows.Select(r => r.InstrumentId).ToList();
    }
}

/// <summary>REAL accounts: live funds (every 30 s) and profile (daily) from Paytm into mst.account_risk_config / mst.trading_accounts.</summary>
public sealed class FundsSync(ConfigCache config, IPaytmMoneyClient client, TokenStore tokens, IAccountRepository accounts, TimeProvider clock, ILogger<FundsSync> log)
{
    public async Task SyncAsync(CancellationToken ct)
    {
        foreach (var a in config.Accounts.Where(a => a.TradeMode == TradeMode.Real))
        {
            if (tokens.ReadToken(a.AccountId) is not { } token) continue;
            try
            {
                var f = await client.GetFundsAsync(token, ct);
                await accounts.UpdateFundsAsync(a.AccountId, f.AvailableMargin, f.UtilisedMargin, f.OpeningBalance, clock.GetUtcNow().UtcDateTime, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Funds sync failed for account {Account}", a.AccountId); }
        }
    }

    public async Task SyncProfilesAsync(CancellationToken ct)
    {
        foreach (var a in config.Accounts.Where(a => a.ApiKey is not null))
        {
            if (tokens.ReadToken(a.AccountId) is not { } token) continue;
            try
            {
                var p = await client.GetProfileAsync(token, ct);
                if (await accounts.GetAsync(a.AccountId, ct) is not { } fresh || string.IsNullOrWhiteSpace(p.Name)) continue;
                fresh.UserName = p.Name.Length > 150 ? p.Name[..150] : p.Name;
                fresh.Email ??= p.Email;
                fresh.Phone ??= p.Phone;
                await accounts.UpdateAsync(fresh, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Profile sync failed for account {Account}", a.AccountId); }
        }
    }
}

/// <summary>Drives the IST trading day: pre-open prep, live session timers and end-of-day summary.</summary>
public sealed class EngineOrchestrator(
    ConfigCache config, TokenStore tokens, IMarketFeed feed, UniverseService universe, CandlePipeline candles, TradeManager trades,
    HistoryBackfill backfill, WatchlistBuilder watchlist, FundsSync funds, IScripMasterSync scrips, IMarketRepository market,
    IConfigRepository configs, ITradingRepository trading, IAlertSender alerts, TimeProvider clock, ILogger<EngineOrchestrator> log)
{
    private static readonly TimeOnly PreOpenAt = new(8, 40), EndOfDayAt = new(15, 35);
    private DateOnly? _preOpenDone, _prepDone, _eodDone;
    private DateTime _lastConfig, _lastUniverse, _lastFunds, _lastPrepAttempt, _feedAlertAt;
    private string? _feedToken;

    public bool Prepared => _prepDone == Ist.Today(clock);

    public async Task StartAsync(CancellationToken ct)
    {
        await config.ReloadAsync(ct);
        await tokens.ReloadAsync(ct);
        await trades.StartAsync(ct);
    }

    public async Task OnTokensUpdatedAsync(CancellationToken ct)
    {
        await tokens.ReloadAsync(ct);
        await EnsureFeedAsync(ct);
    }

    /// <summary>Called every second by the host.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = Ist.Today(clock);
        var time = Ist.Time(clock);

        if ((now - _lastConfig).TotalSeconds >= 60)
        {
            _lastConfig = now;
            await config.ReloadAsync(ct);
            await tokens.ReloadAsync(ct);
        }
        if (config.Calendar.SessionFor(today) is not { } session) return;

        if (time >= PreOpenAt && _preOpenDone != today)
        {
            await PreOpenAsync(today, ct);
            _preOpenDone = today;
        }

        var prepAt = config.Strategy.AuthLoginTime.AddMinutes(5) > new TimeOnly(8, 50) ? config.Strategy.AuthLoginTime.AddMinutes(5) : new TimeOnly(8, 50);
        if (time >= prepAt && time < EndOfDayAt && _prepDone != today && (now - _lastPrepAttempt).TotalSeconds >= 30)
        {
            _lastPrepAttempt = now;
            if (await PrepareAsync(today, ct)) _prepDone = today;
        }

        if (_prepDone == today && time >= session.Open.AddMinutes(-15) && time < session.CloseExclusive.AddMinutes(5))
        {
            candles.Flush(now);
            await trades.OnSecondAsync(ct);
            if ((now - _lastUniverse).TotalSeconds >= 60) { _lastUniverse = now; await universe.RefreshAsync(ct); }
            if ((now - _lastFunds).TotalSeconds >= 30) { _lastFunds = now; await funds.SyncAsync(ct); }
            await WatchFeedAsync(now, time, session, ct);
        }

        if (time >= EndOfDayAt && _eodDone != today && _prepDone == today)
        {
            await EndOfDayAsync(today, ct);
            _eodDone = today;
        }
    }

    private async Task PreOpenAsync(DateOnly today, CancellationToken ct)
    {
        var added = await configs.AddMissingPatternsAsync(PatternRegistry.All.Select(d => PatternRegistry.ToCatalogRow(d.Definition)), ct);
        if (added > 0) log.LogInformation("Seeded {Count} chart patterns into mst.chart_patterns", added);
        if (await market.LastScripSyncAsync(ct) is not { } last || last < today)
        {
            try { await scrips.SyncAsync(today, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Scrip master sync failed");
                await alerts.SendAsync(AlertKind.Critical, "⚠️ Scrip master sync failed; contracts may be stale (MS_SECURITY_MASTER_STALE will block entries).", ct);
            }
        }
        await config.ReloadAsync(ct);
    }

    private async Task<bool> PrepareAsync(DateOnly today, CancellationToken ct)
    {
        await tokens.ReloadAsync(ct);
        if (!await EnsureFeedAsync(ct)) return false;

        foreach (var a in config.Accounts)
            await trading.GetOrCreateDayPnlAsync(a.AccountId, today, a.TradeMode, a.RiskConfig?.AvailableMargin ?? 0, a.RiskConfig?.AvailableMargin ?? 0, ct);
        await trading.GetOrCreateBotStateAsync(today, ct);
        await funds.SyncAsync(ct);
        await funds.SyncProfilesAsync(ct);

        var ids = await watchlist.BuildAsync(ct);
        await universe.SetWatchlistAsync(ids, ct);
        await backfill.RunAsync(config.Instruments.Where(i => ids.Contains(i.InstrumentId)), ct);
        await alerts.SendAsync(AlertKind.Info, $"🤖 Trade bot ready for {today:dd-MMM-yyyy}: {config.Accounts.Count} account(s), {ids.Count} instrument(s) on watch.", ct);
        return true;
    }

    private async Task<bool> EnsureFeedAsync(CancellationToken ct)
    {
        var provider = config.Accounts.FirstOrDefault(a => a.IsDataProvider);
        var token = provider is null ? null : tokens.PublicToken(provider.AccountId);
        if (token is null)
        {
            if ((clock.GetUtcNow().UtcDateTime - _feedAlertAt).TotalMinutes >= 10)
            {
                _feedAlertAt = clock.GetUtcNow().UtcDateTime;
                await alerts.SendAsync(AlertKind.Critical, "⚠️ No valid data-provider token; market feed is down. Complete the login from the dashboard.", ct);
            }
            return false;
        }
        if (token != _feedToken)
        {
            _feedToken = token;
            await feed.StartAsync(token, ct);
            log.LogInformation("Market feed started with data provider {Account}", provider!.AccountId);
        }
        return true;
    }

    private async Task WatchFeedAsync(DateTime now, TimeOnly time, (TimeOnly Open, TimeOnly CloseExclusive) session, CancellationToken ct)
    {
        if (time < session.Open || time >= session.CloseExclusive) return;
        var stale = feed.LastTickUtc is not { } last || (now - last).TotalSeconds > 30;
        if (!stale || (now - _feedAlertAt).TotalMinutes < 10) return;
        _feedAlertAt = now;
        log.LogWarning("Market feed stale (connected={Connected}, last tick {Last})", feed.IsConnected, feed.LastTickUtc);
        await alerts.SendAsync(AlertKind.Critical, $"⚠️ Market feed stale since {feed.LastTickUtc:HH:mm:ss} UTC; entries are blocked (FX_STALE_TICK). Reconnecting.", ct);
        _feedToken = null;
        await EnsureFeedAsync(ct);
    }

    private async Task EndOfDayAsync(DateOnly today, CancellationToken ct)
    {
        candles.Flush(clock.GetUtcNow().UtcDateTime);
        var days = await trading.DayPnlsAsync(today, ct);
        var lines = days.Select(d =>
        {
            var name = config.Account(d.AccountId)?.AccountName ?? $"#{d.AccountId}";
            return $"{name} ({DbValue.ToDb(d.TradeMode)}): trades {d.TotalTrades} (W{d.WinningTrades}/L{d.LosingTrades}) · net ₹{d.RealizedGrossPnl - d.TotalCharges:0.00}";
        });
        await alerts.SendAsync(AlertKind.Info, $"📊 <b>EOD {today:dd-MMM}</b>\n{string.Join("\n", lines)}", ct);
        await trading.AddEventAsync(new SystemEvent { EventType = "END_OF_DAY", Message = $"End of day {today}" }, ct);
    }
}
