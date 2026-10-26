using System.Collections.Concurrent;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

public sealed record CurvePoint(DateTime TimeUtc, decimal Value);

/// <summary>Realized: cumulative net at each exit. Net: realized + MTM − charges, sampled while the engine runs.</summary>
public sealed record PnlCurve(IReadOnlyList<CurvePoint> Realized, IReadOnlyList<CurvePoint> Net);

/// <summary>Intraday PnL curve: in-memory 15 s samples per account, plus realized steps rebuilt from today's trades.</summary>
public sealed class PnlCurveService(ConfigCache config, TradeManager liveTrades, ITradingRepository trading, TimeProvider clock)
{
    public static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<long, List<(DateTime Time, decimal Net)>> _samples = new();
    private DateOnly _day;
    private DateTime _lastUtc;

    /// <summary>Called by the engine every second during the session.</summary>
    public async Task SampleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (now - _lastUtc < SampleEvery) return;
        _lastUtc = now;
        var today = Ist.Today(clock);
        if (_day != today)
        {
            _samples.Clear();
            _day = today;
        }

        var days = (await trading.DayPnlsAsync(today, ct)).ToDictionary(d => d.AccountId);
        foreach (var a in config.Accounts)
        {
            var d = days.GetValueOrDefault(a.AccountId);
            var net = (d?.RealizedGrossPnl ?? 0) + liveTrades.Unrealized(a.AccountId) - (d?.TotalCharges ?? 0);
            var list = _samples.GetOrAdd(a.AccountId, _ => []);
            lock (list) list.Add((now, Math.Round(net, 2)));
        }
    }

    public async Task<PnlCurve> BuildAsync(IReadOnlySet<long> accountIds, CancellationToken ct)
    {
        var trades = (await trading.TradesAsync(Ist.Today(clock), null, ct)).Where(t => accountIds.Contains(t.AccountId)).ToList();
        var closed = trades.Where(t => t.ExitTime is not null && t.TradeStatus != TradeStatus.Open).OrderBy(t => t.ExitTime).ToList();
        var realized = new List<CurvePoint>();
        if (closed.Count > 0) realized.Add(new CurvePoint(trades.Min(t => t.EntryTime), 0));
        decimal cumulative = 0;
        foreach (var t in closed)
        {
            cumulative += t.NetPnl;
            realized.Add(new CurvePoint(t.ExitTime!.Value, Math.Round(cumulative, 2)));
        }

        var net = _samples.Where(kv => accountIds.Contains(kv.Key))
            .SelectMany(kv => { lock (kv.Value) return kv.Value.ToList(); })
            .GroupBy(p => p.Time).OrderBy(g => g.Key)
            .Select(g => new CurvePoint(g.Key, g.Sum(p => p.Net))).ToList();
        return new PnlCurve(realized, net);
    }
}

/// <summary>Candles for charts: persisted bars, overlaid with the in-memory history and the forming bar.</summary>
public sealed class CandleQueryService(IMarketRepository market, BarHistory history, CandlePipeline candles)
{
    public async Task<List<Bar>> GetAsync(SeriesKey series, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var merged = (await market.RecentBarsAsync(series, tf, fromUtc, ct)).ToDictionary(b => b.Time);
        foreach (var b in history.Get(series, tf).Where(b => b.Time >= fromUtc)) merged[b.Time] = b;
        if (candles.Forming(series, tf) is { } f && f.Time >= fromUtc) merged[f.Time] = f;
        return merged.Values.Where(b => b.Time <= toUtc).OrderBy(b => b.Time).ToList();
    }
}
