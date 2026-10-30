using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Application.Engine;
using TradeBot.Application.News;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

internal static class WorkerFixture
{
    public static readonly DateOnly Monday = new(2026, 11, 2);
    public static readonly DateOnly Saturday = new(2026, 10, 31);

    public static FakeTimeProvider ClockAt(DateOnly date, int hour, int minute) =>
        new(new DateTimeOffset(Ist.ToUtc(date, new TimeOnly(hour, minute)), TimeSpan.Zero));

    public static void SetIst(this FakeTimeProvider clock, DateOnly date, int hour, int minute) =>
        clock.SetUtcNow(new DateTimeOffset(Ist.ToUtc(date, new TimeOnly(hour, minute)), TimeSpan.Zero));

    public static Instrument Nifty => new() { InstrumentId = 1, Symbol = "NIFTY", InstrumentType = InstrumentType.Index };
    public static Instrument BankNifty => new() { InstrumentId = 2, Symbol = "BANKNIFTY", InstrumentType = InstrumentType.Index };
    public static Instrument Reliance => new() { InstrumentId = 10, Symbol = "RELIANCE", InstrumentType = InstrumentType.Stock };

    public static StockCompany Company(long id, string symbol, long? instrumentId = null, decimal nifty = 0, decimal bank = 0, bool fno = true) =>
        new() { CompanyId = id, NseSymbol = symbol, CompanyName = symbol + " Ltd", InstrumentId = instrumentId, Nifty50WeightPct = nifty, BankniftyWeightPct = bank, IsFnoActive = fno };

    public static CompanyDailyNews News(int score, ImpactLevel impact, long? companyId = null, long? instrumentId = null, string headline = "h") =>
        new() { Headline = headline, ContentHash = headline, CompanyId = companyId, InstrumentId = instrumentId, SentimentScore = score, ImpactLevel = impact };

    public static AiNewsItem Item(string headline, string? nse = null, string? index = null, int score = 40, ImpactLevel impact = ImpactLevel.Medium, params string[] urls) =>
        new(nse, index, headline, "summary", NewsCategory.Other, score >= 0 ? MarketBias.Bullish : MarketBias.Bearish, score, impact, null, urls);

    public static TradingAccount Account(long id, TradeMode mode = TradeMode.Real, bool provider = false, bool keys = true, string? totp = "JBSWY3DPEHPK3PXP", bool active = true) =>
        new()
        {
            AccountId = id, AccountName = $"Acct {id}", ClientId = $"C{id}", UserName = "u", TradeMode = mode, IsDataProvider = provider, IsActive = active,
            ApiKey = keys ? "key" : null, ApiSecret = keys ? "secret" : null, TotpSecret = totp, LoginId = "9876543210", LoginPassword = "pw",
        };
}

public class SentimentAggregatorTests
{
    private static readonly Dictionary<long, StockCompany> Companies = new()
    {
        [1] = WorkerFixture.Company(1, "RELIANCE", instrumentId: 10, nifty: 9.5m),
        [2] = WorkerFixture.Company(2, "HDFCBANK", nifty: 12m, bank: 28m),
        [3] = WorkerFixture.Company(3, "TINYCO", nifty: 0.4m),
    };

    private static DailyMarketSentiment Compute(Instrument inst, bool autoPause = true, params CompanyDailyNews[] news) =>
        SentimentAggregator.Compute(inst, news, Companies, autoPause, WorkerFixture.Monday);

    [Fact]
    public void Stock_sentiment_is_impact_weighted_average_of_its_own_news()
    {
        var s = Compute(WorkerFixture.Reliance, true, WorkerFixture.News(80, ImpactLevel.High, companyId: 1), WorkerFixture.News(-40, ImpactLevel.Low, companyId: 1));
        Assert.Equal(56m, s.NetSentimentScore);
        Assert.Equal(MarketBias.Bullish, s.DominantBias);
        Assert.False(s.TradingHaltActive);
    }

    [Fact]
    public void Unrelated_news_is_ignored_and_neutral()
    {
        var s = Compute(WorkerFixture.Reliance, true, WorkerFixture.News(-90, ImpactLevel.High, companyId: 2));
        Assert.Equal(0m, s.NetSentimentScore);
        Assert.Equal(MarketBias.Neutral, s.DominantBias);
        Assert.Equal("[]", s.ActiveCatalysts);
    }

    [Fact]
    public void Index_weights_constituents_and_macro()
    {
        // HDFCBANK: relevance 1.2 x HIGH 1 = 1.2; macro: 0.6 x MEDIUM 0.5 = 0.3 → (-50*1.2 + 20*0.3)/1.5 = -36
        var s = Compute(WorkerFixture.Nifty, true, WorkerFixture.News(-50, ImpactLevel.High, companyId: 2), WorkerFixture.News(20, ImpactLevel.Medium));
        Assert.Equal(-36m, s.NetSentimentScore);
        Assert.Equal(MarketBias.Bearish, s.DominantBias);
    }

    [Fact]
    public void Extreme_news_halts_only_where_relevant()
    {
        var heavy = WorkerFixture.News(-100, ImpactLevel.ExtremeHalt, companyId: 2, headline: "Regulator bars HDFCBANK " + new string('x', 600));
        var light = WorkerFixture.News(-100, ImpactLevel.ExtremeHalt, companyId: 3, headline: "TINYCO fraud");

        var nifty = Compute(WorkerFixture.Nifty, true, heavy);
        Assert.True(nifty.TradingHaltActive);
        Assert.Equal(500, nifty.HaltReason!.Length);
        Assert.StartsWith("EXTREME news: Regulator bars HDFCBANK", nifty.HaltReason);

        Assert.False(Compute(WorkerFixture.Nifty, true, light).TradingHaltActive);
        Assert.False(Compute(WorkerFixture.Reliance, true, heavy).TradingHaltActive);
    }

    [Fact]
    public void Extreme_macro_respects_auto_pause_setting()
    {
        var macro = WorkerFixture.News(-100, ImpactLevel.ExtremeHalt, headline: "Circuit breaker hit market-wide");
        Assert.True(Compute(WorkerFixture.Reliance, true, macro).TradingHaltActive);
        Assert.False(Compute(WorkerFixture.Reliance, false, macro).TradingHaltActive);
    }
}

public class NewsRowMappingTests
{
    private static readonly Dictionary<string, StockCompany> Companies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HDFCBANK"] = WorkerFixture.Company(2, "HDFCBANK", nifty: 12m, bank: 28m),
    };

    private static readonly Dictionary<string, Instrument> Instruments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = WorkerFixture.Nifty, ["BANKNIFTY"] = WorkerFixture.BankNifty,
    };

    private static CompanyDailyNews Row(AiNewsItem item, params string[] grounding) =>
        NewsIntelligenceService.ToRow(item, NewsScanType.Premarket, WorkerFixture.Monday, new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc), Companies, Instruments, grounding);

    [Fact]
    public void Company_news_maps_company_and_index_weighted_impact()
    {
        var r = Row(WorkerFixture.Item("Q2 beats", nse: "hdfcbank", score: 50));
        Assert.Equal(2, r.CompanyId);
        Assert.Null(r.InstrumentId);
        Assert.Equal(6m, r.NiftyWeightedImpact);
        Assert.Equal(14m, r.BankniftyWeightedImpact);
        Assert.Equal(new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc), r.AnalyzedAt);
    }

    [Fact]
    public void Index_and_market_wide_news_map_to_instrument_or_macro()
    {
        var bank = Row(WorkerFixture.Item("RBI on bank NPAs", index: "BANKNIFTY", score: -30));
        Assert.Equal(2, bank.InstrumentId);
        Assert.Equal(0m, bank.NiftyWeightedImpact);
        Assert.Equal(-30m, bank.BankniftyWeightedImpact);

        var macro = Row(WorkerFixture.Item("US futures slide", score: -20));
        Assert.Null(macro.InstrumentId);
        Assert.Null(macro.CompanyId);
        Assert.Equal(-20m, macro.NiftyWeightedImpact);
        Assert.Equal(-20m, macro.BankniftyWeightedImpact);
    }

    [Fact]
    public void Source_urls_are_validated_merged_and_capped()
    {
        var urls = Enumerable.Range(0, 12).Select(i => $"https://news.example/{i}").Append("not a url").ToArray();
        var r = Row(WorkerFixture.Item("x", urls: urls), "https://news.example/0", "https://grounding.example/a");
        var stored = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.SourceUrls)!;
        Assert.Equal(10, stored.Count);
        Assert.DoesNotContain("not a url", stored);
        Assert.Equal(stored.Distinct().Count(), stored.Count);
    }

    [Fact]
    public void Content_hash_ignores_case_and_spacing_but_not_target()
    {
        var a = Row(WorkerFixture.Item("Reliance  wins ORDER", nse: "HDFCBANK"));
        var b = Row(WorkerFixture.Item("reliance wins order", nse: "HDFCBANK"));
        var c = Row(WorkerFixture.Item("reliance wins order", index: "NIFTY"));
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }
}

/// <summary>NewsIntelligenceService + NewsScheduler against in-memory fakes.</summary>
public sealed class NewsHarness
{
    public FakeTimeProvider Clock { get; }
    public Stub<INewsIntelligenceClient> Ai { get; } = Stub<INewsIntelligenceClient>.Create();
    public Stub<IConfigRepository> Configs { get; } = Stub<IConfigRepository>.Create();
    public Stub<IMarketRepository> Market { get; } = Stub<IMarketRepository>.Create();
    public Stub<IIntelligenceRepository> Intel { get; } = Stub<IIntelligenceRepository>.Create();
    public Stub<ITradingRepository> Trading { get; } = Stub<ITradingRepository>.Create();
    public Stub<IEventBus> Bus { get; } = Stub<IEventBus>.Create();
    public Stub<IAlertSender> Alerts { get; } = Stub<IAlertSender>.Create();
    public GeminiNewsConfig Gemini { get; } = new() { ModelName = "gemini-test", GeminiApiKey = "k", MaxCallsPerMinute = 30 };
    public List<StockCompany> Companies { get; } = [];
    public List<CompanyDailyNews> Stored { get; } = [];
    public NewsIntelligenceService Service { get; }
    public NewsScheduler Scheduler { get; }
    private int _n;

    public NewsHarness(FakeTimeProvider clock)
    {
        Clock = clock;
        Ai.On(nameof(INewsIntelligenceClient.ScanAsync), _ =>
            new AiNewsResult([WorkerFixture.Item($"headline {Interlocked.Increment(ref _n)}")], [], "{}", false));
        Configs.On(nameof(IConfigRepository.GeminiAsync), _ => Gemini);
        Market.On(nameof(IMarketRepository.CompaniesAsync), _ => Companies.ToList());
        Market.On(nameof(IMarketRepository.InstrumentsAsync), _ => new List<Instrument> { WorkerFixture.Nifty, WorkerFixture.BankNifty, WorkerFixture.Reliance });
        Intel.On(nameof(IIntelligenceRepository.AddNewsAsync), a =>
        {
            var rows = (IReadOnlyCollection<CompanyDailyNews>)a[0]!;
            Stored.AddRange(rows);
            return rows.Count;
        });
        Intel.On(nameof(IIntelligenceRepository.NewsAsync), _ => Stored.ToList());
        Service = new NewsIntelligenceService(Ai.Object, Configs.Object, Market.Object, Intel.Object, Trading.Object, Bus.Object, Alerts.Object, clock, NullLogger<NewsIntelligenceService>.Instance);
        Scheduler = new NewsScheduler(Service, Configs.Object, Intel.Object, clock, NullLogger<NewsScheduler>.Instance);
    }

    public int AiCalls => Ai.Calls(nameof(INewsIntelligenceClient.ScanAsync)).Count;
    public List<NewsScanType> ScanTypes => Stored.Select(s => s.ScanType).ToList();
}

public class NewsIntelligenceServiceTests
{
    [Fact]
    public async Task Premarket_scan_batches_companies_and_recomputes_sentiment()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 30));
        h.Companies.AddRange(Enumerable.Range(1, 65).Select(i => WorkerFixture.Company(i, $"S{i}")));
        h.Companies.Add(WorkerFixture.Company(99, "NOTFNO", fno: false));

        var added = await h.Service.PremarketScanAsync(CancellationToken.None);

        Assert.Equal(1 + 3, h.AiCalls);
        Assert.Equal(4, added);
        Assert.All(h.Stored, n => Assert.Equal(NewsScanType.Premarket, n.ScanType));
        Assert.DoesNotContain(h.Ai.Calls(nameof(INewsIntelligenceClient.ScanAsync)), c => ((string)c[2]!).Contains("NOTFNO"));
        Assert.Equal(3, h.Intel.Calls(nameof(IIntelligenceRepository.UpsertSentimentAsync)).Count);
        Assert.Contains(h.Bus.Calls(nameof(IEventBus.PublishAsync)), c => (string)c[0]! == EventChannels.NewsUpdated);
    }

    [Fact]
    public async Task Missing_api_key_skips_gemini()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        h.Gemini.GeminiApiKey = null;
        Assert.Equal(0, await h.Service.IntradayCheckAsync(null, NewsScanType.Manual, CancellationToken.None));
        Assert.Equal(0, h.AiCalls);
    }

    [Fact]
    public async Task Gemini_failure_is_contained()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        h.Ai.On(nameof(INewsIntelligenceClient.ScanAsync), _ => Task.FromException<AiNewsResult>(new HttpRequestException("503")));
        Assert.Equal(0, await h.Service.IntradayCheckAsync(null, NewsScanType.Periodic, CancellationToken.None));
    }

    [Fact]
    public async Task Extreme_items_raise_critical_alerts()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        h.Ai.On(nameof(INewsIntelligenceClient.ScanAsync), _ =>
            new AiNewsResult([WorkerFixture.Item("SEBI <bans> trading", impact: ImpactLevel.ExtremeHalt, score: -100)], [], "{}", false));
        await h.Service.IntradayCheckAsync(null, NewsScanType.IntradaySpike, CancellationToken.None);
        var alert = Assert.Single(h.Alerts.Calls(nameof(IAlertSender.SendAsync)));
        Assert.Equal(AlertKind.Critical, alert[0]);
        Assert.Contains("SEBI &lt;bans&gt; trading", (string)alert[1]!);
        var ev = (SystemEvent)Assert.Single(h.Trading.Calls(nameof(ITradingRepository.AddEventAsync)))[0]!;
        Assert.Equal(("NEWS_EXTREME", EventSeverity.Critical), (ev.EventType, ev.Severity));
    }

    [Fact]
    public async Task Stock_check_uses_company_prompt_and_unknown_company_is_skipped()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        Assert.Equal(0, await h.Service.IntradayCheckAsync(10, NewsScanType.IntradaySpike, CancellationToken.None));
        Assert.Equal(0, h.AiCalls);

        h.Companies.Add(WorkerFixture.Company(1, "RELIANCE", instrumentId: 10));
        await h.Service.IntradayCheckAsync(10, NewsScanType.IntradaySpike, CancellationToken.None);
        Assert.Contains("RELIANCE (RELIANCE Ltd)", (string)Assert.Single(h.Ai.Calls(nameof(INewsIntelligenceClient.ScanAsync)))[2]!);
    }

    [Fact]
    public async Task Calls_are_throttled_to_max_per_minute()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        h.Gemini.MaxCallsPerMinute = 2;
        await h.Service.IntradayCheckAsync(null, NewsScanType.Manual, CancellationToken.None);
        await h.Service.IntradayCheckAsync(null, NewsScanType.Manual, CancellationToken.None);

        var third = h.Service.IntradayCheckAsync(null, NewsScanType.Manual, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, h.AiCalls);

        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, h.AiCalls);
    }
}

public class NewsSchedulerTests
{
    [Theory]
    [InlineData("123", 123L, false)]
    [InlineData("manual:all", null, true)]
    [InlineData("manual:7", 7L, true)]
    [InlineData("all", null, false)]
    public void Parses_request_payloads(string payload, long? id, bool manual) =>
        Assert.Equal(new NewsScheduler.NewsRequest(id, manual), NewsScheduler.Parse(payload));

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("manual:")]
    public void Rejects_malformed_payloads(string payload) => Assert.Null(NewsScheduler.Parse(payload));

    [Fact]
    public async Task Premarket_scan_runs_once_at_configured_time()
    {
        var clock = WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 29);
        var h = new NewsHarness(clock);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(0, h.AiCalls);

        clock.SetIst(WorkerFixture.Monday, 8, 30);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(1, h.AiCalls);

        clock.SetIst(WorkerFixture.Monday, 8, 40);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(1, h.AiCalls);
    }

    [Fact]
    public async Task Restart_does_not_repeat_todays_premarket_scan()
    {
        var h = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        h.Stored.Add(new CompanyDailyNews { Headline = "earlier", ContentHash = "e", ScanType = NewsScanType.Premarket, TradingDate = WorkerFixture.Monday });
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(0, h.AiCalls);
    }

    [Fact]
    public async Task Weekend_and_inactive_config_do_nothing()
    {
        var weekend = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Saturday, 10, 0));
        weekend.Scheduler.Enqueue("manual:all");
        await weekend.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(0, weekend.AiCalls);
        Assert.Equal(0, weekend.Scheduler.Pending);

        var inactive = new NewsHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 10, 0));
        inactive.Gemini.IsActive = false;
        await inactive.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(0, inactive.AiCalls);
    }

    [Fact]
    public async Task Queued_requests_are_coalesced_deduplicated_and_typed()
    {
        var clock = WorkerFixture.ClockAt(WorkerFixture.Monday, 9, 0);
        var h = new NewsHarness(clock);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(1, h.AiCalls);
        h.Companies.Add(WorkerFixture.Company(1, "RELIANCE", instrumentId: 10));

        clock.SetIst(WorkerFixture.Monday, 9, 20);
        h.Scheduler.Enqueue("10");
        h.Scheduler.Enqueue("10");
        h.Scheduler.Enqueue("manual:all");
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(3, h.AiCalls);
        Assert.Equal(new[] { NewsScanType.Premarket, NewsScanType.Manual, NewsScanType.IntradaySpike }, h.ScanTypes);

        clock.SetIst(WorkerFixture.Monday, 9, 21);
        h.Scheduler.Enqueue("10");
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(3, h.AiCalls);

        clock.SetIst(WorkerFixture.Monday, 9, 23);
        h.Scheduler.Enqueue("10");
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(4, h.AiCalls);
    }

    [Fact]
    public async Task Periodic_macro_scan_runs_during_session_only()
    {
        var clock = WorkerFixture.ClockAt(WorkerFixture.Monday, 9, 0);
        var h = new NewsHarness(clock);
        await h.Scheduler.TickAsync(CancellationToken.None);

        clock.SetIst(WorkerFixture.Monday, 9, 10);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(1, h.AiCalls);

        clock.SetIst(WorkerFixture.Monday, 9, 16);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(NewsScanType.Periodic, h.ScanTypes[^1]);

        clock.SetIst(WorkerFixture.Monday, 9, 25);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(2, h.AiCalls);

        clock.SetIst(WorkerFixture.Monday, 15, 31);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(2, h.AiCalls);
    }
}

/// <summary>BrokerAuthService + AuthScheduler against in-memory fakes.</summary>
public sealed class AuthHarness
{
    public FakeTimeProvider Clock { get; }
    public Stub<IAccountRepository> Accounts { get; } = Stub<IAccountRepository>.Create();
    public Stub<IConfigRepository> Configs { get; } = Stub<IConfigRepository>.Create();
    public Stub<IPaytmMoneyClient> Paytm { get; } = Stub<IPaytmMoneyClient>.Create();
    public Stub<IBrokerLoginAutomation> Browser { get; } = Stub<IBrokerLoginAutomation>.Create();
    public Stub<ITradingRepository> Trading { get; } = Stub<ITradingRepository>.Create();
    public Stub<IEventBus> Bus { get; } = Stub<IEventBus>.Create();
    public Stub<IAlertSender> Alerts { get; } = Stub<IAlertSender>.Create();
    public StrategyConfig Strategy { get; } = new() { AuthMaxRetries = 0 };
    public List<TradingAccount> AccountList { get; } = [];
    public List<AccountAuthToken> Tokens { get; } = [];
    public BrokerConnectionChecker Connection { get; }
    public BrokerAuthService Service { get; }
    public AuthScheduler Scheduler { get; }

    public AuthHarness(FakeTimeProvider clock)
    {
        Clock = clock;
        Accounts.On(nameof(IAccountRepository.ListAsync), _ => AccountList.Where(a => a.IsActive).ToList());
        Accounts.On(nameof(IAccountRepository.GetAsync), a => AccountList.FirstOrDefault(x => x.AccountId == (long)a[0]!));
        Accounts.On(nameof(IAccountRepository.AddTokenAsync), a => { Tokens.Add((AccountAuthToken)a[0]!); return Task.CompletedTask; });
        Accounts.On(nameof(IAccountRepository.LatestTokensAsync), _ =>
            Tokens.GroupBy(t => t.AccountId).ToDictionary(g => g.Key, g => g.Last()));
        Configs.On(nameof(IConfigRepository.StrategyAsync), _ => Strategy);
        Paytm.On(nameof(IPaytmMoneyClient.LoginUrl), a => $"https://login.example/?apiKey={a[0]}&state={a[1]}");
        Paytm.On(nameof(IPaytmMoneyClient.GenerateTokensAsync), _ => new BrokerTokens("access", "public", "read", null));
        Paytm.On(nameof(IPaytmMoneyClient.GetProfileAsync), _ => new BrokerProfile("C7", "Asha Trader", "asha@example.in", "9876543210", "{}"));
        Browser.On(nameof(IBrokerLoginAutomation.GetRequestTokenAsync), _ => "req-token");
        Connection = new BrokerConnectionChecker(Paytm.Object, clock, NullLogger<BrokerConnectionChecker>.Instance);
        Service = new BrokerAuthService(Accounts.Object, Configs.Object, Paytm.Object, Browser.Object, Connection, Trading.Object, Bus.Object, Alerts.Object, clock,
            NullLogger<BrokerAuthService>.Instance);
        Scheduler = new AuthScheduler(Service, Configs.Object, Accounts.Object, clock, NullLogger<AuthScheduler>.Instance);
    }

    public List<string> EventTypes => Trading.Calls(nameof(ITradingRepository.AddEventAsync)).Select(c => ((SystemEvent)c[0]!).EventType).ToList();

    public List<long> LoggedIn => Browser.Calls(nameof(IBrokerLoginAutomation.GetRequestTokenAsync)).Select(c => ((TradingAccount)c[0]!).AccountId).ToList();
}

public class BrokerAuthServiceTests
{
    [Fact]
    public void Only_real_or_data_provider_accounts_with_keys_need_login()
    {
        Assert.True(BrokerAuthService.NeedsLogin(WorkerFixture.Account(1)));
        Assert.True(BrokerAuthService.NeedsLogin(WorkerFixture.Account(2, TradeMode.Paper, provider: true)));
        Assert.False(BrokerAuthService.NeedsLogin(WorkerFixture.Account(3, TradeMode.Paper)));
        Assert.False(BrokerAuthService.NeedsLogin(WorkerFixture.Account(4, keys: false)));
        Assert.False(BrokerAuthService.NeedsLogin(WorkerFixture.Account(5, active: false)));
    }

    [Fact]
    public async Task Successful_login_stores_tokens_and_notifies()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.AccountList.Add(WorkerFixture.Account(7));

        Assert.True(await h.Service.LoginAsync(7, CancellationToken.None));

        var t = Assert.Single(h.Tokens);
        Assert.Equal((LoginStatus.Success, "access", "public", "read", 1), (t.LoginStatus, t.XJwtToken, t.PublicAccessToken, t.ReadAccessToken, t.AttemptCount));
        Assert.Equal(Ist.ToUtc(WorkerFixture.Monday, new TimeOnly(23, 59)), t.TokenExpiry);
        Assert.Equal("req-token", h.Paytm.Calls(nameof(IPaytmMoneyClient.GenerateTokensAsync)).Single()[2]);
        Assert.Contains(h.Bus.Calls(nameof(IEventBus.PublishAsync)), c => (string)c[0]! == EventChannels.TokensUpdated && (string)c[1]! == "7");
        Assert.Empty(h.Alerts.Calls(nameof(IAlertSender.SendAsync)));
        Assert.Equal(new[] { "LOGIN_SUCCESS" }, h.EventTypes);

        // Verified with GET /accounts/v1/user/details using the new access token before it was stored
        Assert.Equal("access", h.Paytm.Calls(nameof(IPaytmMoneyClient.GetProfileAsync)).Single()[0]);
        var ev = (SystemEvent)h.Trading.Calls(nameof(ITradingRepository.AddEventAsync)).Single()[0]!;
        Assert.Contains("verified with GET /accounts/v1/user/details as Asha Trader (client C7)", ev.Message);
        Assert.True(h.Connection.Last(7)!.Ok);
    }

    [Fact]
    public async Task A_token_paytm_rejects_on_user_details_fails_the_login_without_retrying()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.Strategy.AuthMaxRetries = 3;
        h.AccountList.Add(WorkerFixture.Account(7));
        h.Paytm.On(nameof(IPaytmMoneyClient.GetProfileAsync), _ => Task.FromException<BrokerProfile>(
            new BrokerApiException("Paytm API returned 401.", 401, "PM_MERCHANT_AUTH_401669", "Invalid Token", "{}")));

        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        Assert.Single(h.LoggedIn);
        var t = Assert.Single(h.Tokens);
        Assert.Equal(LoginStatus.Failed, t.LoginStatus);
        Assert.Contains("PM_MERCHANT_AUTH_401669 Invalid Token", t.ErrorMessage);
        Assert.True(h.Connection.Last(7)!.TokenRejected);
    }

    [Fact]
    public async Task User_details_for_another_client_id_alerts_but_keeps_the_token()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.AccountList.Add(WorkerFixture.Account(7));
        h.Paytm.On(nameof(IPaytmMoneyClient.GetProfileAsync), _ => new BrokerProfile("C99", "Someone Else", null, null, "{}"));

        Assert.True(await h.Service.LoginAsync(7, CancellationToken.None));
        Assert.Equal(LoginStatus.Success, Assert.Single(h.Tokens).LoginStatus);
        Assert.Equal(new[] { "LOGIN_SUCCESS", "LOGIN_CLIENT_MISMATCH" }, h.EventTypes);
        Assert.Contains("client C99", (string)Assert.Single(h.Alerts.Calls(nameof(IAlertSender.SendAsync)))[1]!);
    }

    [Fact]
    public async Task Login_errors_are_stored_and_alerted_without_keys_secrets_or_tokens()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        var a = WorkerFixture.Account(7);
        a.ApiKey = "live-api-key-0001";
        a.ApiSecret = "live-api-secret-9999";
        h.AccountList.Add(a);
        // Playwright quotes the URL it was loading; a broker body may echo a token
        h.Browser.On(nameof(IBrokerLoginAutomation.GetRequestTokenAsync), _ => Task.FromException<string>(new TimeoutException(
            "Timeout 15000ms exceeded navigating to \"https://login.paytmmoney.com/merchant-login?apiKey=live-api-key-0001&state=tb7\" " +
            "secret live-api-secret-9999 {\"access_token\":\"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc\"}")));

        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        var stored = Assert.Single(h.Tokens).ErrorMessage!;
        foreach (var leak in new[] { "live-api-key-0001", "live-api-secret-9999", "eyJhbGciOiJIUzI1NiJ9" })
        {
            Assert.DoesNotContain(leak, stored);
            Assert.DoesNotContain(leak, ((SystemEvent)h.Trading.Calls(nameof(ITradingRepository.AddEventAsync)).Single()[0]!).Message);
        }
        Assert.Contains("Timeout 15000ms exceeded", stored);
        Assert.Contains("apiKey=[redacted]", stored);
    }

    [Fact]
    public async Task A_drifting_server_clock_is_named_when_paytm_rejects_the_totp()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.AccountList.Add(WorkerFixture.Account(7));
        h.Paytm.On(nameof(IPaytmMoneyClient.ProbeClockAsync), _ => new BrokerClockReading(TimeSpan.FromSeconds(42), DateTime.UtcNow));
        h.Browser.On(nameof(IBrokerLoginAutomation.GetRequestTokenAsync), _ => Task.FromException<string>(new BrokerLoginException("Paytm rejected the TOTP code.")));

        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        Assert.Contains("server clock is 42 s behind Paytm's", Assert.Single(h.Tokens).ErrorMessage);
    }

    [Fact]
    public async Task Failed_login_retries_then_records_failure_and_alerts()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.Strategy.AuthMaxRetries = 2;
        h.AccountList.Add(WorkerFixture.Account(7));
        h.Browser.On(nameof(IBrokerLoginAutomation.GetRequestTokenAsync), _ => Task.FromException<string>(new TimeoutException("OTP field not found")));

        var login = h.Service.LoginAsync(7, CancellationToken.None);
        for (var i = 0; i < 100 && !login.IsCompleted; i++)
        {
            await Task.Delay(10);
            h.Clock.Advance(BrokerAuthService.RetryDelay);
        }

        Assert.False(await login.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, h.LoggedIn.Count);
        var t = Assert.Single(h.Tokens);
        Assert.Equal((LoginStatus.Failed, 3, "OTP field not found"), (t.LoginStatus, t.AttemptCount, t.ErrorMessage));
        var alert = Assert.Single(h.Alerts.Calls(nameof(IAlertSender.SendAsync)));
        Assert.Equal(AlertKind.Critical, alert[0]);
        Assert.Contains("https://login.example/?apiKey=key&state=tb7", (string)alert[1]!);
        Assert.Contains(h.Bus.Calls(nameof(IEventBus.PublishAsync)), c => (string)c[0]! == EventChannels.TokensUpdated);
        Assert.Equal(new[] { "LOGIN_FAILED" }, h.EventTypes);
    }

    [Fact]
    public async Task Missing_totp_fails_fast_without_browser()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.Strategy.AuthMaxRetries = 3;
        h.AccountList.Add(WorkerFixture.Account(7, totp: null));

        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        Assert.Empty(h.LoggedIn);
        Assert.Equal(1, Assert.Single(h.Tokens).AttemptCount);
    }

    [Theory]
    [InlineData(null, "pw", "login ID")]
    [InlineData("9876543210", null, "login password")]
    [InlineData("9876543210", "pw", "not Base32", "JBSWY3DP-EHPK3PXP")]
    [InlineData("9876543210", "pw", "a clean API key", "JBSWY3DPEHPK3PXP", "key with space")]
    [InlineData("9876543210", "pw", "API secret", "JBSWY3DPEHPK3PXP", "key", "   ")]
    public async Task Incomplete_credentials_fail_before_any_browser_or_otp(string? loginId, string? password, string expected, string totp = "JBSWY3DPEHPK3PXP",
        string apiKey = "key", string apiSecret = "secret")
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.Strategy.AuthMaxRetries = 3;
        var a = WorkerFixture.Account(7, totp: totp);
        a.LoginId = loginId;
        a.LoginPassword = password;
        a.ApiKey = apiKey;
        a.ApiSecret = apiSecret;
        h.AccountList.Add(a);

        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        Assert.Empty(h.LoggedIn);
        Assert.Contains(expected, Assert.Single(h.Tokens).ErrorMessage);
    }

    [Fact]
    public async Task Sms_otp_screen_is_not_retried()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.Strategy.AuthMaxRetries = 3;
        h.AccountList.Add(WorkerFixture.Account(7));
        h.Browser.On(nameof(IBrokerLoginAutomation.GetRequestTokenAsync), _ => Task.FromException<string>(new BrokerLoginException("Paytm asked for the SMS OTP")));

        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        // One browser run, one OTP at most; the alert carries the reason and the manual link
        Assert.Single(h.LoggedIn);
        Assert.Equal(1, Assert.Single(h.Tokens).AttemptCount);
        Assert.Contains("SMS OTP", (string)Assert.Single(h.Alerts.Calls(nameof(IAlertSender.SendAsync)))[1]!);
    }

    [Fact]
    public async Task Paper_account_is_not_logged_in()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.AccountList.Add(WorkerFixture.Account(7, TradeMode.Paper));
        Assert.False(await h.Service.LoginAsync(7, CancellationToken.None));
        Assert.Empty(h.Tokens);
    }
}

public class AuthSchedulerTests
{
    [Fact]
    public async Task Daily_login_runs_once_at_configured_time_for_accounts_needing_tokens()
    {
        var clock = WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 44);
        var h = new AuthHarness(clock);
        h.AccountList.AddRange([WorkerFixture.Account(1), WorkerFixture.Account(2, TradeMode.Paper, provider: true), WorkerFixture.Account(3, TradeMode.Paper), WorkerFixture.Account(4)]);
        h.Tokens.Add(new AccountAuthToken { AccountId = 4, LoginStatus = LoginStatus.Success, CreatedAt = Ist.ToUtc(WorkerFixture.Monday, new TimeOnly(8, 0)) });

        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Empty(h.LoggedIn);

        clock.SetIst(WorkerFixture.Monday, 8, 45);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(new[] { 1L, 2L }, h.LoggedIn);

        clock.SetIst(WorkerFixture.Monday, 9, 0);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(2, h.LoggedIn.Count);
    }

    [Fact]
    public async Task Yesterdays_token_does_not_count()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Monday, 8, 45));
        h.AccountList.Add(WorkerFixture.Account(1));
        h.Tokens.Add(new AccountAuthToken { AccountId = 1, LoginStatus = LoginStatus.Success, CreatedAt = Ist.ToUtc(WorkerFixture.Monday.AddDays(-3), new TimeOnly(8, 45)) });
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(new[] { 1L }, h.LoggedIn);
    }

    [Fact]
    public async Task No_daily_login_on_weekends_but_requests_are_served()
    {
        var h = new AuthHarness(WorkerFixture.ClockAt(WorkerFixture.Saturday, 9, 0));
        h.AccountList.AddRange([WorkerFixture.Account(1), WorkerFixture.Account(2)]);
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Empty(h.LoggedIn);

        h.Scheduler.Enqueue("2");
        h.Scheduler.Enqueue("2");
        h.Scheduler.Enqueue("junk");
        await h.Scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(new[] { 2L }, h.LoggedIn);
        Assert.Equal(0, h.Scheduler.Pending);
    }
}
