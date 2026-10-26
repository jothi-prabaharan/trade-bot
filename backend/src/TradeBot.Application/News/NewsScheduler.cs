using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.News;

/// <summary>
/// News worker timing on trading days: one pre-market scan at mst.gemini_news_config.premarket_scan_time,
/// periodic macro scans during the session, and queued volume-spike / manual checks (deduplicated per target).
/// </summary>
public sealed class NewsScheduler(NewsIntelligenceService news, IConfigRepository configs, IIntelligenceRepository intel, TimeProvider clock, ILogger<NewsScheduler> log)
{
    public static readonly TimeSpan RequestDedup = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ConfigRefresh = TimeSpan.FromMinutes(5);
    public TimeSpan MacroInterval { get; init; } = TimeSpan.FromMinutes(15);

    private readonly ConcurrentQueue<NewsRequest> _requests = new();
    private readonly Dictionary<NewsRequest, DateTime> _lastRun = [];
    private GeminiNewsConfig? _gemini;
    private MarketCalendar _calendar = MarketCalendar.WeekendsOnly;
    private DateTime _configLoadedUtc = DateTime.MinValue;
    private DateOnly? _premarketDoneFor;
    private DateTime _lastMacroUtc = DateTime.MinValue;

    public readonly record struct NewsRequest(long? InstrumentId, bool Manual);

    public int Pending => _requests.Count;

    /// <summary>Payload from tradebot_news_check: "{id}" (volume spike) or "manual:{id|all}".</summary>
    public static NewsRequest? Parse(string payload)
    {
        var manual = payload.StartsWith("manual:", StringComparison.Ordinal);
        var target = manual ? payload["manual:".Length..] : payload;
        if (target == "all") return new NewsRequest(null, manual);
        return long.TryParse(target, out var id) && id > 0 ? new NewsRequest(id, manual) : null;
    }

    public void Enqueue(string payload)
    {
        if (Parse(payload) is { } r) _requests.Enqueue(r);
        else log.LogWarning("Ignoring malformed news check payload {Payload}", payload);
    }

    public void InvalidateConfig() => _configLoadedUtc = DateTime.MinValue;

    public async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        if (nowUtc - _configLoadedUtc >= ConfigRefresh)
        {
            _gemini = await configs.GeminiAsync(ct);
            _calendar = new MarketCalendar(await configs.HolidaysAsync(ct));
            _configLoadedUtc = nowUtc;
        }

        var today = Ist.Today(clock);
        var time = Ist.Time(clock);
        if (_gemini is not { IsActive: true } || _calendar.SessionFor(today) is not { } session || time >= session.CloseExclusive)
        {
            _requests.Clear();
            return;
        }

        if (_premarketDoneFor != today && time >= _gemini.PremarketScanTime)
        {
            _premarketDoneFor = today;
            if (!(await intel.NewsAsync(today, null, 5000, ct)).Any(n => n.ScanType == NewsScanType.Premarket))
                await news.PremarketScanAsync(ct);
            _lastMacroUtc = nowUtc;
            return;
        }

        if (time < session.Open) { _requests.Clear(); return; }

        // Coalesce the queue: one scan per distinct target per tick
        var batch = new HashSet<NewsRequest>();
        while (_requests.TryDequeue(out var r)) batch.Add(r);
        foreach (var r in batch.OrderBy(r => r.InstrumentId is null ? 0 : 1))
        {
            if (_lastRun.TryGetValue(r, out var last) && nowUtc - last < RequestDedup) continue;
            _lastRun[r] = nowUtc;
            if (r.InstrumentId is null) _lastMacroUtc = nowUtc;
            await news.IntradayCheckAsync(r.InstrumentId, r.Manual ? NewsScanType.Manual : NewsScanType.IntradaySpike, ct);
        }

        if (nowUtc - _lastMacroUtc >= MacroInterval)
        {
            _lastMacroUtc = nowUtc;
            await news.IntradayCheckAsync(null, NewsScanType.Periodic, ct);
        }
    }
}
