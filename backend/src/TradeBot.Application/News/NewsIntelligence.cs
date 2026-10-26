using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.News;

public static class NewsPrompts
{
    private const string Rules = """
        Rules: use Google Search; report only real, verifiable items with sources; never invent news.
        sentiment_score is the expected price impact from -100 (very bearish) to +100 (very bullish).
        impact_level: LOW (minor), MEDIUM (notable), HIGH (likely to move the price today);
        EXTREME_HALT only for trading halts, fraud/regulatory bans or circuit-level shocks.
        published_at is ISO-8601 UTC when known. Return an empty items array when nothing material happened.
        """;

    public static string Companies(DateOnly date, IEnumerable<StockCompany> companies, int lookbackHours) => $"""
        You are a financial news analyst for Indian equity derivatives (NSE F&O).
        As of {date:dd-MMM-yyyy} IST, find material company-specific news from the last {lookbackHours} hours for these NSE companies:
        {string.Join("; ", companies.Select(c => $"{c.NseSymbol} ({c.CompanyName})"))}.
        Relevant: earnings, guidance, large orders, regulatory/legal action, M&A, management change, rating change, block deals.
        Set nse_symbol exactly as listed; leave index_symbol null.
        {Rules}
        """;

    public static string Macro(DateOnly date, int lookbackHours) => $"""
        You are a macro strategist for Indian index derivatives.
        As of {date:dd-MMM-yyyy} IST, find market-moving news from the last {lookbackHours} hours affecting NIFTY and BANKNIFTY:
        RBI/monetary policy, inflation and macro data, FII/DII flows, global indices and US futures, crude oil, USD/INR, banking-sector
        developments, government policy and geopolitics. Set index_symbol to NIFTY or BANKNIFTY when the item targets one index,
        or null when it is market-wide; leave nse_symbol null.
        {Rules}
        """;
}

/// <summary>Weighted per-instrument sentiment from today's news (pure; stored in trn.daily_market_sentiment).</summary>
public static class SentimentAggregator
{
    public static decimal ImpactFactor(ImpactLevel i) => i switch
    {
        ImpactLevel.Low => 0.25m, ImpactLevel.Medium => 0.5m, ImpactLevel.High => 1m, _ => 1.5m,
    };

    public static decimal IndexWeight(Instrument index, StockCompany c) => index.Symbol switch
    {
        "NIFTY" => c.Nifty50WeightPct, "BANKNIFTY" => c.BankniftyWeightPct, "FINNIFTY" => c.FinniftyWeightPct, _ => 0,
    };

    public static DailyMarketSentiment Compute(Instrument inst, IReadOnlyList<CompanyDailyNews> news, IReadOnlyDictionary<long, StockCompany> companies,
        bool autoPauseOnExtremeMacro, DateOnly date)
    {
        var weighted = new List<(CompanyDailyNews N, decimal W, bool HaltRelevant)>();
        foreach (var n in news)
        {
            var company = n.CompanyId is { } cid ? companies.GetValueOrDefault(cid) : null;
            var macro = n.CompanyId is null && n.InstrumentId is null;
            decimal relevance;
            bool haltRelevant;
            if (n.InstrumentId == inst.InstrumentId) (relevance, haltRelevant) = (1m, true);
            else if (company is not null && company.InstrumentId == inst.InstrumentId) (relevance, haltRelevant) = (1m, true);
            else if (company is not null && inst.InstrumentType == InstrumentType.Index && IndexWeight(inst, company) is var w and > 0)
                (relevance, haltRelevant) = (Math.Min(1.5m, w / 10), w >= FixedLimits.NewsVetoMinConstituentWeightPct);
            else if (macro) (relevance, haltRelevant) = (inst.InstrumentType == InstrumentType.Index ? 0.6m : 0.3m, autoPauseOnExtremeMacro);
            else continue;
            weighted.Add((n, relevance * ImpactFactor(n.ImpactLevel), haltRelevant));
        }

        var totalW = weighted.Sum(x => x.W);
        var net = totalW == 0 ? 0 : Math.Clamp(Math.Round(weighted.Sum(x => x.N.SentimentScore * x.W) / totalW, 2), -100, 100);
        var halt = weighted.FirstOrDefault(x => x.HaltRelevant && x.N.ImpactLevel == ImpactLevel.ExtremeHalt).N;
        var catalysts = weighted.OrderByDescending(x => Math.Abs(x.N.SentimentScore) * x.W).Take(5)
            .Select(x => new { newsId = x.N.NewsId, headline = x.N.Headline, sentiment = DbValue.ToDb(x.N.Sentiment), impact = DbValue.ToDb(x.N.ImpactLevel), score = x.N.SentimentScore });

        return new DailyMarketSentiment
        {
            TradingDate = date, InstrumentId = inst.InstrumentId, NetSentimentScore = net,
            DominantBias = net >= 15 ? MarketBias.Bullish : net <= -15 ? MarketBias.Bearish : MarketBias.Neutral,
            ActiveCatalysts = JsonSerializer.Serialize(catalysts),
            TradingHaltActive = halt is not null,
            HaltReason = halt is null ? null : $"EXTREME news: {halt.Headline}" is var r && r.Length > 500 ? r[..500] : r,
        };
    }
}

/// <summary>Gemini scans → trn.company_daily_news → trn.daily_market_sentiment → NOTIFY tradebot_news_updated.</summary>
public sealed class NewsIntelligenceService(
    INewsIntelligenceClient ai, IConfigRepository configs, IMarketRepository market, IIntelligenceRepository intel,
    IEventBus bus, IAlertSender alerts, TimeProvider clock, ILogger<NewsIntelligenceService> log)
{
    public const int CompaniesPerPrompt = 30;

    private readonly Queue<DateTime> _calls = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> PremarketScanAsync(CancellationToken ct)
    {
        var companies = (await market.CompaniesAsync(ct)).Where(c => c.IsFnoActive || c.Nifty50WeightPct > 0 || c.BankniftyWeightPct > 0).ToList();
        var today = Ist.Today(clock);
        var added = await ScanAsync(NewsPrompts.Macro(today, 18), NewsScanType.Premarket, ct);
        foreach (var batch in companies.Chunk(CompaniesPerPrompt))
            added += await ScanAsync(NewsPrompts.Companies(today, batch, 24), NewsScanType.Premarket, ct);
        await RecomputeSentimentAsync(ct);
        log.LogInformation("Pre-market scan stored {Count} news items for {Companies} companies", added, companies.Count);
        return added;
    }

    /// <summary>Volume-spike, periodic or manual check: one instrument (its company, or index macro) or the whole market.</summary>
    public async Task<int> IntradayCheckAsync(long? instrumentId, NewsScanType scanType, CancellationToken ct)
    {
        var today = Ist.Today(clock);
        var instruments = await market.InstrumentsAsync(false, ct);
        var inst = instrumentId is { } id ? instruments.FirstOrDefault(i => i.InstrumentId == id) : null;
        string prompt;
        if (inst is { InstrumentType: InstrumentType.Stock })
        {
            var company = (await market.CompaniesAsync(ct)).FirstOrDefault(c => c.InstrumentId == inst.InstrumentId || c.NseSymbol == inst.Symbol);
            if (company is null) return 0;
            prompt = NewsPrompts.Companies(today, [company], 3);
        }
        else prompt = NewsPrompts.Macro(today, 3);

        var added = await ScanAsync(prompt, scanType, ct);
        await RecomputeSentimentAsync(ct);
        return added;
    }

    private async Task<int> ScanAsync(string prompt, NewsScanType scanType, CancellationToken ct)
    {
        var cfg = await configs.GeminiAsync(ct);
        if (cfg?.GeminiApiKey is not { Length: > 0 } key)
        {
            log.LogWarning("Gemini API key not configured; news scan skipped");
            return 0;
        }

        AiNewsResult result;
        await ThrottleAsync(cfg.MaxCallsPerMinute, ct);
        try { result = await ai.ScanAsync(cfg, key, prompt, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Gemini scan failed");
            return 0;
        }

        var today = Ist.Today(clock);
        var companies = (await market.CompaniesAsync(ct)).ToDictionary(c => c.NseSymbol, StringComparer.OrdinalIgnoreCase);
        var instruments = (await market.InstrumentsAsync(false, ct)).ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var rows = result.Items.Select(item => ToRow(item, scanType, today, nowUtc, companies, instruments, result.GroundingUrls)).ToList();
        var added = await intel.AddNewsAsync(rows, ct);

        foreach (var extreme in rows.Where(r => r.ImpactLevel == ImpactLevel.ExtremeHalt))
            await alerts.SendAsync(AlertKind.Critical, $"🚨 EXTREME news: {System.Net.WebUtility.HtmlEncode(extreme.Headline)}", ct);
        return added;
    }

    /// <summary>Sliding one-minute window over Gemini calls (mst.gemini_news_config.max_calls_per_minute).</summary>
    private async Task ThrottleAsync(int perMinute, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                while (_calls.TryPeek(out var t) && now - t >= TimeSpan.FromMinutes(1)) _calls.Dequeue();
                if (_calls.Count < Math.Max(1, perMinute)) { _calls.Enqueue(now); return; }
                await Task.Delay(_calls.Peek().AddMinutes(1) - now, clock, ct);
            }
        }
        finally { _gate.Release(); }
    }

    internal static CompanyDailyNews ToRow(AiNewsItem item, NewsScanType scanType, DateOnly today, DateTime analyzedAtUtc,
        IReadOnlyDictionary<string, StockCompany> companies, IReadOnlyDictionary<string, Instrument> instruments, IReadOnlyList<string> grounding)
    {
        var company = item.NseSymbol is { } s ? companies.GetValueOrDefault(s) : null;
        var index = item.IndexSymbol is { } x ? instruments.GetValueOrDefault(x) : null;
        var score = item.SentimentScore;
        var urls = item.SourceUrls.Concat(grounding).Where(u => Uri.TryCreate(u, UriKind.Absolute, out _)).Distinct().Take(10).ToList();
        return new CompanyDailyNews
        {
            TradingDate = today,
            CompanyId = company?.CompanyId,
            InstrumentId = company is null ? index?.InstrumentId : null,
            ScanType = scanType,
            Headline = item.Headline,
            Summary = item.Summary,
            SourceUrls = JsonSerializer.Serialize(urls),
            NewsCategory = item.Category,
            Sentiment = item.Sentiment,
            SentimentScore = score,
            ImpactLevel = item.Impact,
            NiftyWeightedImpact = company is not null ? Math.Round(score * company.Nifty50WeightPct / 100, 4) : index?.Symbol == "NIFTY" || index is null ? score : 0,
            BankniftyWeightedImpact = company is not null ? Math.Round(score * company.BankniftyWeightPct / 100, 4) : index?.Symbol == "BANKNIFTY" || index is null ? score : 0,
            ContentHash = Hash($"{item.NseSymbol ?? item.IndexSymbol ?? "MKT"}|{Normalize(item.Headline)}"),
            GeminiStructuredOutput = JsonSerializer.Serialize(item),
            PublishedAt = item.PublishedAtUtc,
            AnalyzedAt = analyzedAtUtc,
        };
    }

    public async Task RecomputeSentimentAsync(CancellationToken ct)
    {
        var today = Ist.Today(clock);
        var news = await intel.NewsAsync(today, null, 5000, ct);
        var companies = (await market.CompaniesAsync(ct)).ToDictionary(c => c.CompanyId);
        var cfg = await configs.GeminiAsync(ct);
        foreach (var inst in await market.InstrumentsAsync(false, ct))
            await intel.UpsertSentimentAsync(SentimentAggregator.Compute(inst, news, companies, cfg?.AutoPauseOnExtremeMacro ?? true, today), ct);
        await bus.PublishAsync(EventChannels.NewsUpdated, "all", ct);
    }

    private static string Normalize(string s) => string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
