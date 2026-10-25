using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Patterns;
using TradeBot.Application.Trading;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

/// <summary>Ticks → multi-timeframe candles → history + batched persistence + pattern scan + volume-spike news checks.</summary>
public sealed class CandlePipeline(MarketDataHub hub, BarHistory history, IMarketRepository market, IEventBus bus,
    ConfigCache config, TimeProvider clock, ILogger<CandlePipeline> log)
{
    private readonly CandleAggregator _aggregator = new(Timeframes.Intraday);
    private readonly Channel<ClosedBar> _persist = Channel.CreateUnbounded<ClosedBar>();
    private readonly Channel<ClosedBar> _scan = Channel.CreateUnbounded<ClosedBar>();
    private readonly ConcurrentDictionary<long, DateTime> _lastSpikeCheck = new();
    private readonly Lock _aggLock = new();

    public ChannelReader<ClosedBar> ClosedBars => _scan.Reader;

    public void OnTick(Tick t)
    {
        if (hub.SeriesFor(t.SecurityId) is not { } series) return;
        List<ClosedBar> closed;
        lock (_aggLock) closed = _aggregator.OnTick(series, t).ToList();
        Handle(closed);
    }

    public void Flush(DateTime nowUtc)
    {
        List<ClosedBar> closed;
        lock (_aggLock) closed = _aggregator.Flush(nowUtc).ToList();
        Handle(closed);
    }

    public Bar? Forming(SeriesKey series, Timeframe tf)
    {
        lock (_aggLock) return _aggregator.Forming(series, tf);
    }

    private void Handle(List<ClosedBar> closed)
    {
        foreach (var c in closed)
        {
            history.Add(c.Series, c.Timeframe, c.Bar);
            _persist.Writer.TryWrite(c);
            if (c.Series.SecurityId is null) _scan.Writer.TryWrite(c);
            if (c.Series.SecurityId is null && c.Timeframe == Timeframe.M5) CheckSpike(c.Series);
        }
    }

    private void CheckSpike(SeriesKey series)
    {
        var bars = history.Get(series, Timeframe.M5);
        if (!Indicators.IsVolumeSpike(bars, config.Gemini.VolumeSpikeTriggerMultiplier)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        if (_lastSpikeCheck.TryGetValue(series.InstrumentId, out var last) && now - last < TimeSpan.FromMinutes(15)) return;
        _lastSpikeCheck[series.InstrumentId] = now;
        _ = bus.PublishAsync(EventChannels.NewsCheckRequested, series.InstrumentId.ToString(), CancellationToken.None);
    }

    /// <summary>Writes closed bars to trn.ohlcv_candles every two seconds.</summary>
    public async Task RunPersistenceAsync(CancellationToken ct)
    {
        var batch = new List<ClosedBar>();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
            while (_persist.Reader.TryRead(out var c)) batch.Add(c);
            if (batch.Count == 0) continue;
            try
            {
                await market.SaveCandlesAsync(batch, ct);
                batch.Clear();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Saving {Count} candles failed; will retry", batch.Count);
                if (batch.Count > 20_000) batch.RemoveRange(0, batch.Count - 20_000);
            }
        }
    }
}

/// <summary>Runs the pattern engine on each closed trigger-timeframe bar of an underlying and hands confirmations to the signal service.</summary>
public sealed class PatternScanner
{
    private readonly ConfigCache _config;
    private readonly BarHistory _history;
    private readonly SignalService _signals;
    private readonly ILogger<PatternScanner> _log;
    private PatternEngine _engine;

    public PatternScanner(ConfigCache config, BarHistory history, SignalService signals, ILogger<PatternScanner> log)
    {
        (_config, _history, _signals, _log) = (config, history, signals, log);
        _engine = new PatternEngine(config.Patterns);
        config.Reloaded += () => _engine.Reload(_config.Patterns);
    }

    public async Task RunAsync(ChannelReader<ClosedBar> bars, CancellationToken ct)
    {
        await foreach (var c in bars.ReadAllAsync(ct))
        {
            if (c.Timeframe != _config.Strategy.TriggerTimeframe) continue;
            try
            {
                var trend = Indicators.TrendBias(_history.Get(c.Series, _config.Strategy.TrendTimeframe));
                var confirmed = _engine.OnBarClosed(c.Series, c.Timeframe, _history.Get(c.Series, c.Timeframe), trend);
                foreach (var p in confirmed) await _signals.OnPatternAsync(p, c.Series, trend, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "Pattern scan failed for instrument {Instrument}", c.Series.InstrumentId);
            }
        }
    }
}

/// <summary>Confirmed underlying pattern → ATM option → mandatory bracket + scores → trn.signals → dispatcher.</summary>
public sealed class SignalService(
    ConfigCache config, MarketDataHub hub, UniverseService universe, IPaytmMoneyClient client, TokenStore tokens,
    IIntelligenceRepository intel, IMarketRepository market, Dispatcher dispatcher, TimeProvider clock, ILogger<SignalService> log)
{
    private readonly ConcurrentDictionary<(long, DateOnly), (DateTime At, OiSnapshot? Oi)> _oiCache = new();

    public async Task OnPatternAsync(ConfirmedPattern p, SeriesKey series, MarketBias trend, CancellationToken ct)
    {
        if (config.Instrument(series.InstrumentId) is not { IsActive: true } instrument) return;
        if (config.Patterns.FirstOrDefault(r => r.PatternCode == p.Code) is not { } row) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var strategy = config.Strategy;

        var detection = await intel.AddDetectionAsync(new DetectedPattern
        {
            InstrumentId = instrument.InstrumentId, PatternId = row.PatternId, Timeframe = p.Timeframe, TrendTimeframe = strategy.TrendTimeframe, TrendBias = trend,
            Direction = p.Direction, NeckOrBreakoutPrice = p.BreakoutPrice, StructuralSlPrice = p.StructuralSl, StructuralTargetPrice = p.StructuralTarget,
            PivotCoordinates = JsonSerializer.Serialize(p.Pivots.Select(x => new { t = x.Time, price = x.Price, high = x.IsHigh })),
            ConfidenceScore = p.Confidence, IsConfirmed = true, BreakoutCandleTime = p.ConfirmedBarTime, DetectedAt = now,
        }, ct);

        var aligned = (p.Direction == PatternDirection.Bullish && trend == MarketBias.Bullish) || (p.Direction == PatternDirection.Bearish && trend == MarketBias.Bearish);
        var ist = Ist.Time(clock);
        if (!instrument.IsTradable || !aligned || ist < strategy.EntryStartTime || ist > strategy.EntryCutoffTime)
        {
            log.LogDebug("Detection {Detection} {Code} not traded (aligned={Aligned}, tradable={Tradable})", detection.DetectionId, p.Code, aligned, instrument.IsTradable);
            return;
        }

        var today = Ist.Today(clock);
        var contracts = await market.ContractsAsync(instrument.InstrumentId, today, ct);
        var options = contracts.Where(c => c.SecurityType is SecurityType.OptIdx or SecurityType.OptStk && c.ExpiryDate is not null).ToList();
        if (ContractSelector.SelectExpiry(options.Select(o => o.ExpiryDate!.Value), today, strategy.ExpiryPolicy) is not { } expiry) return;
        var type = SignalBuilder.OptionFor(p.Direction);
        var chain = options.Where(o => o.ExpiryDate == expiry).ToList();
        var underlying = hub.UnderlyingPrice(instrument.InstrumentId) ?? p.EntryReference;
        var futures = hub.FuturesPrice(instrument.InstrumentId) ?? p.EntryReference;
        var option = strategy.StrikeSelection == StrikeSelection.DeltaTarget
            ? ContractSelector.SelectByDelta(chain.Where(c => c.OptType == type).Select(c => (c, DeltaOf(c, futures, now))).Where(x => x.Item2 is not null).Select(x => (x.c, x.Item2!.Value)))
            : ContractSelector.SelectStrike(chain, underlying, type, strategy.StrikeSelection);
        if (option is null) return;

        await universe.EnsureAsync(option, ct);
        var quote = hub.Last(option.SecurityId);
        if (quote is null || (now - quote.Value.TimeUtc).TotalSeconds > 3)
        {
            await RefreshQuoteAsync(option, ct);
            quote = hub.Last(option.SecurityId);
        }
        if (quote is not { } q) { log.LogInformation("No quote for {Option}; signal skipped", option.PmlSymbol); return; }

        if (strategy.RequireOptionConfirmation && (q.AverageTradedPrice is not { } vwap || q.Ltp <= vwap))
        {
            log.LogInformation("{Option} premium {Ltp} not above its VWAP {Vwap}; no confirmation", option.PmlSymbol, q.Ltp, q.AverageTradedPrice);
            return;
        }

        var bias = p.Direction == PatternDirection.Bullish ? MarketBias.Bullish : MarketBias.Bearish;
        var sentiment = await intel.SentimentAsync(today, instrument.InstrumentId, ct);
        var catalyst = await intel.TopCatalystAsync(today, instrument.InstrumentId, bias, ct);
        var built = SignalBuilder.Build(new SignalInputs(p, detection.DetectionId, instrument, option, futures, underlying, q.Ltp, now,
            sentiment?.NetSentimentScore, catalyst?.NewsId, await OiAsync(instrument, expiry, underlying, ct), config.Gemini, BracketPolicy.From(config.ReferenceRisk)));

        var signal = built.Signal;
        if (signal.CombinedAiConfidence < strategy.MinCombinedConfidence)
        {
            signal.Status = SignalStatus.Rejected;
            signal.RejectionReason = $"Combined confidence {signal.CombinedAiConfidence} below {strategy.MinCombinedConfidence}";
        }
        signal = await intel.AddSignalAsync(signal, ct);
        log.LogInformation("Signal {Signal}: {Code} → BUY {Option} @ {Ltp} SL {Sl} T {Target} conf {Conf}",
            signal.SignalId, p.Code, option.PmlSymbol, q.Ltp, signal.MandatorySlPrice, signal.MandatoryTargetPrice, signal.CombinedAiConfidence);
        if (signal.Status == SignalStatus.Rejected) return;

        await dispatcher.DispatchAsync(new SignalContext(signal, detection, row, instrument, option, catalyst?.Headline), ct);
    }

    private double? DeltaOf(SecurityMaster c, decimal futures, DateTime now) =>
        hub.Last(c.SecurityId) is { } t && c.StrikePrice is { } k && c.ExpiryDate is { } e
            ? Black76.ImpliedVol((double)t.Ltp, (double)futures, (double)k, Black76.YearsToExpiry(e, now), c.OptType == OptionType.Ce) is { } iv
                ? Black76.Compute((double)futures, (double)k, Black76.YearsToExpiry(e, now), iv, c.OptType == OptionType.Ce).Delta : null
            : null;

    private async Task RefreshQuoteAsync(SecurityMaster option, CancellationToken ct)
    {
        var provider = config.Accounts.FirstOrDefault(a => a.IsDataProvider) ?? config.Accounts.FirstOrDefault();
        if (provider is null || tokens.ReadToken(provider.AccountId) is not { } token) return;
        try
        {
            foreach (var t in await client.GetLiveQuotesAsync(token, [new FeedSubscription(option.SecurityId, ScripType.Option)], ct)) hub.OnTick(t);
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Live quote refresh failed for {Option}", option.PmlSymbol); }
    }

    private async Task<OiSnapshot?> OiAsync(Instrument instrument, DateOnly expiry, decimal underlying, CancellationToken ct)
    {
        var key = (instrument.InstrumentId, expiry);
        if (_oiCache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds < 60) return cached.Oi;
        var provider = config.Accounts.FirstOrDefault(a => a.IsDataProvider) ?? config.Accounts.FirstOrDefault();
        OiSnapshot? oi = null;
        if (provider is not null && tokens.ReadToken(provider.AccountId) is { } token)
        {
            try
            {
                var calls = await client.GetOptionChainAsync(token, instrument.Symbol, OptionType.Ce, expiry, ct);
                var puts = await client.GetOptionChainAsync(token, instrument.Symbol, OptionType.Pe, expiry, ct);
                var step = instrument.StrikeStep ?? 50;
                bool Near(OptionChainRow r, int n) => Math.Abs(r.Strike - underlying) <= n * step;
                var callOi = calls.Where(r => Near(r, 5)).Sum(r => r.OpenInterest ?? 0);
                var putOi = puts.Where(r => Near(r, 5)).Sum(r => r.OpenInterest ?? 0);
                oi = callOi > 0 ? new OiSnapshot(Math.Round((decimal)putOi / callOi, 3), calls.Where(r => Near(r, 2)).Sum(r => r.OiChange ?? 0), puts.Where(r => Near(r, 2)).Sum(r => r.OiChange ?? 0)) : null;
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Option chain fetch failed for {Symbol}", instrument.Symbol); }
        }
        _oiCache[key] = (DateTime.UtcNow, oi);
        return oi;
    }
}

/// <summary>Keeps the feed subscribed to: index/stock spot, near-month future, ATM±N options and every open position.</summary>
public sealed class UniverseService(ConfigCache config, IMarketRepository market, MarketDataHub hub, IMarketFeed feed,
    TradeManager trades, TimeProvider clock, ILogger<UniverseService> log)
{
    private readonly HashSet<FeedSubscription> _subscribed = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<long> _watchlist = [];

    public IReadOnlyCollection<long> Watchlist => _watchlist;

    public async Task SetWatchlistAsync(IEnumerable<long> instrumentIds, CancellationToken ct)
    {
        _watchlist = instrumentIds.Distinct().ToList();
        await RefreshAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var today = Ist.Today(clock);
            var wanted = new HashSet<FeedSubscription>();
            foreach (var inst in config.Instruments.Where(i => i.IsActive && i.IsTradable && (i.InstrumentType == InstrumentType.Index || _watchlist.Contains(i.InstrumentId))))
            {
                var contracts = await market.ContractsAsync(inst.InstrumentId, today, ct);
                var spot = inst.UnderlyingSecurityId is { } sid ? contracts.FirstOrDefault(c => c.SecurityId == sid) : null;
                if (inst.UnderlyingSecurityId is { } spotId)
                {
                    hub.Register(new ScripInfo(spotId, inst.InstrumentId, SeriesRole.Spot, spot));
                    wanted.Add(new FeedSubscription(spotId, inst.InstrumentType == InstrumentType.Index ? ScripType.Index : ScripType.Equity));
                }
                var future = contracts.Where(c => c.SecurityType is SecurityType.FutIdx or SecurityType.FutStk).MinBy(c => c.ExpiryDate);
                if (future is not null)
                {
                    hub.Register(new ScripInfo(future.SecurityId, inst.InstrumentId, SeriesRole.Future, future));
                    wanted.Add(new FeedSubscription(future.SecurityId, ScripType.Future));
                }

                var price = hub.UnderlyingPrice(inst.InstrumentId);
                var options = contracts.Where(c => c.SecurityType is SecurityType.OptIdx or SecurityType.OptStk && c.ExpiryDate is not null).ToList();
                if (price is null || ContractSelector.SelectExpiry(options.Select(o => o.ExpiryDate!.Value), today, config.Strategy.ExpiryPolicy) is not { } expiry) continue;
                var width = inst.InstrumentType == InstrumentType.Index ? 2 : 1;
                foreach (var type in new[] { OptionType.Ce, OptionType.Pe })
                {
                    var strikes = options.Where(o => o.ExpiryDate == expiry && o.OptType == type).OrderBy(o => o.StrikePrice).ToList();
                    if (strikes.Count == 0) continue;
                    var atm = strikes.Select((s, i) => (s, i)).MinBy(x => Math.Abs(x.s.StrikePrice!.Value - price.Value)).i;
                    foreach (var o in strikes.Skip(Math.Max(0, atm - width)).Take(2 * width + 1))
                    {
                        hub.Register(new ScripInfo(o.SecurityId, inst.InstrumentId, SeriesRole.Option, o));
                        wanted.Add(new FeedSubscription(o.SecurityId, ScripType.Option));
                    }
                }
            }
            foreach (var t in trades.OpenTrades) wanted.Add(new FeedSubscription(t.Trade.SecurityId, ScripType.Option));

            var add = wanted.Except(_subscribed).ToList();
            var remove = _subscribed.Except(wanted).ToList();
            if (add.Count > 0) await feed.SubscribeAsync(add, ct);
            if (remove.Count > 0) await feed.UnsubscribeAsync(remove, ct);
            _subscribed.ExceptWith(remove);
            _subscribed.UnionWith(add);
            if (add.Count + remove.Count > 0) log.LogInformation("Universe: +{Add} −{Remove} ({Total} scrips)", add.Count, remove.Count, _subscribed.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureAsync(SecurityMaster option, CancellationToken ct)
    {
        hub.Register(new ScripInfo(option.SecurityId, option.InstrumentId, SeriesRole.Option, option));
        var sub = new FeedSubscription(option.SecurityId, ScripType.Option);
        await _gate.WaitAsync(ct);
        try
        {
            if (_subscribed.Add(sub)) await feed.SubscribeAsync([sub], ct);
        }
        finally { _gate.Release(); }
    }
}
