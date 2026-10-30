using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.External;
using TradeBot.Infrastructure.External.Gemini;
using TradeBot.Infrastructure.External.Paytm;

namespace TradeBot.Infrastructure.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];
    /// <summary>Response headers such as Date and Age.</summary>
    public Action<HttpResponseMessage>? Decorate { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        var (status, text) = respond(request, body);
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        Decorate?.Invoke(response);
        return response;
    }
}

/// <summary>Collects formatted log lines, to prove what reaches the logs.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
}

public sealed class PaytmTickParserTests
{
    private static byte[] Packet(byte type, Action<byte[]> fill)
    {
        var body = new byte[PaytmTickParser.BodySize(type)];
        fill(body);
        return [type, .. body];
    }

    [Fact]
    public void Parses_ltp_full_and_index_packets_in_one_frame()
    {
        var ltp = Packet(PaytmTickParser.Ltp, b => { BinaryPrimitives.WriteSingleLittleEndian(b, 101.35f); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 35001); });
        var full = Packet(PaytmTickParser.Full, b =>
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(100), 25012.5f);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(108), 53001);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(122), 1_234_500);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(166), 987_650);
        });
        var index = Packet(PaytmTickParser.IndexFull, b => { BinaryPrimitives.WriteSingleLittleEndian(b, 24990.15f); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 13); });
        var now = DateTime.UtcNow;

        var ticks = PaytmTickParser.Parse([.. ltp, .. full, .. index], now);

        Assert.Equal(3, ticks.Count);
        Assert.Equal((35001L, 101.35m), (ticks[0].SecurityId, ticks[0].Ltp));
        Assert.Equal((53001L, 25012.5m, 1_234_500L, 987_650L), (ticks[1].SecurityId, ticks[1].Ltp, ticks[1].CumulativeVolume!.Value, ticks[1].OpenInterest!.Value));
        Assert.Equal((13L, 24990.15m), (ticks[2].SecurityId, ticks[2].Ltp));
        Assert.All(ticks, t => Assert.Equal(now, t.TimeUtc));
    }

    [Fact]
    public void Stops_at_unknown_or_truncated_packet()
    {
        var ltp = Packet(PaytmTickParser.Ltp, b => { BinaryPrimitives.WriteSingleLittleEndian(b, 50f); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 7); });
        Assert.Single(PaytmTickParser.Parse([.. ltp, 99, 1, 2, 3], DateTime.UtcNow));
        Assert.Empty(PaytmTickParser.Parse(ltp[..10], DateTime.UtcNow));
    }
}

public sealed class PaytmClientTests
{
    private static (PaytmMoneyClient Client, FakeHandler Handler) Client(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond)
    {
        var h = new FakeHandler(respond);
        return (new PaytmMoneyClient(new HttpClient(h), Options.Create(new PaytmMoneyOptions()), NullLogger<PaytmMoneyClient>.Instance), h);
    }

    private static (PaytmMoneyClient Client, FakeHandler Handler, ListLogger<PaytmMoneyClient> Log) Logged(
        Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond, Action<HttpResponseMessage>? decorate = null, TimeProvider? clock = null)
    {
        var h = new FakeHandler(respond) { Decorate = decorate };
        var log = new ListLogger<PaytmMoneyClient>();
        return (new PaytmMoneyClient(new HttpClient(h), Options.Create(new PaytmMoneyOptions()), log, clock), h, log);
    }

    [Fact]
    public async Task Gettoken_sends_the_three_fields_trimmed_of_pasted_whitespace()
    {
        var (c, h, _) = Logged((_, _) => (HttpStatusCode.OK, """{"access_token":"a.b.c","public_access_token":"P","read_access_token":"R"}"""));
        await c.GenerateTokensAsync(" key-123\n", "\u200Bsecret-456 ", " req-789 ", default);

        var (req, body) = h.Calls.Single();
        Assert.Equal((HttpMethod.Post, "https://developer.paytmmoney.com/accounts/v2/gettoken"), (req.Method, req.RequestUri!.ToString()));
        var json = JsonNode.Parse(body!)!.AsObject();
        Assert.Equal(["api_key", "api_secret_key", "request_token"], json.Select(p => p.Key));
        Assert.Equal(("key-123", "secret-456", "req-789"), (json["api_key"]!.GetValue<string>(), json["api_secret_key"]!.GetValue<string>(), json["request_token"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("key 123", "secret", "req", "API key contains spaces")]
    [InlineData("key", "", "req", "API secret is empty")]
    [InlineData("key", "secret", null, "request token is empty")]
    public async Task Gettoken_with_a_blank_or_broken_field_is_not_sent(string apiKey, string apiSecret, string? requestToken, string expected)
    {
        var (c, h, _) = Logged((_, _) => (HttpStatusCode.OK, "{}"));
        var e = await Assert.ThrowsAsync<BrokerLoginException>(() => c.GenerateTokensAsync(apiKey, apiSecret, requestToken!, default));
        Assert.Contains(expected, e.Message);
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.OK)]   // FAILURE status inside a 200
    public async Task Auth_failures_carry_paytm_code_and_are_logged_clearly_without_secrets(HttpStatusCode status)
    {
        var (c, _, log) = Logged((_, _) => (status,
            """{"status":"FAILURE","message":"Invalid Api Key or Api Secret Key or Request Token","error_code":"PM_MERCHANT_AUTH_400607","data":{"api_secret_key":"live-secret-123456"}}"""));

        var e = await Assert.ThrowsAsync<BrokerApiException>(() => c.GenerateTokensAsync("live-key-123456", "live-secret-123456", "live-request-123456", default));
        Assert.Equal(("PM_MERCHANT_AUTH_400607", "Invalid Api Key or Api Secret Key or Request Token"), (e.ErrorCode, e.BrokerMessage));
        Assert.DoesNotContain("live-secret-123456", e.Body);

        var error = Assert.Single(log.Lines, l => l.Level == LogLevel.Error).Message;
        Assert.Contains("Paytm authentication failed on /accounts/v2/gettoken", error);
        Assert.Contains("PM_MERCHANT_AUTH_400607 Invalid Api Key or Api Secret Key or Request Token", error);
        Assert.All(log.Lines, l => Assert.DoesNotContain("live-", l.Message));
    }

    [Fact]
    public async Task User_details_uses_the_access_token_as_x_jwt_token()
    {
        var (c, h, _) = Logged((_, _) => (HttpStatusCode.OK,
            """{"status":"SUCCESS","data":{"user_id":"PM12345","kyc_name":"Asha Trader","name":"Asha Trader","email":"asha@example.in","mobile":"9876543210"}}"""));
        var p = await c.GetProfileAsync("access-jwt", default);

        var req = h.Calls.Single().Request;
        Assert.Equal((HttpMethod.Get, "https://developer.paytmmoney.com/accounts/v1/user/details"), (req.Method, req.RequestUri!.ToString()));
        Assert.Equal("access-jwt", req.Headers.GetValues("x-jwt-token").Single());
        Assert.Equal(("PM12345", "Asha Trader", "asha@example.in", "9876543210"), (p.ClientId, p.Name, p.Email, p.Phone));
    }

    [Fact]
    public async Task A_rejected_token_on_user_details_is_flagged_as_such()
    {
        var (c, _, log) = Logged((_, _) => (HttpStatusCode.Unauthorized, """{"status":"FAILURE","message":"Invalid Token","error_code":"PM_MERCHANT_AUTH_401669"}"""));
        var e = await Assert.ThrowsAsync<BrokerApiException>(() => c.GetProfileAsync("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig", default));
        Assert.True(e.TokenRejected);
        Assert.Equal((401, "PM_MERCHANT_AUTH_401669"), (e.StatusCode, e.ErrorCode));
        Assert.Contains(log.Lines, l => l.Message.Contains("Invalid Token (the access token is invalid or expired; log in again)"));
        Assert.All(log.Lines, l => Assert.DoesNotContain("eyJhbGci", l.Message));
    }

    [Fact]
    public async Task Paytm_clock_is_read_from_the_date_header_and_probed_with_head()
    {
        var local = new DateTimeOffset(2026, 11, 2, 3, 15, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(local);
        // Paytm is 42 s ahead of us: our clock is behind
        var (c, h, _) = Logged((_, _) => (HttpStatusCode.OK, "{}"), r => r.Headers.Date = local.AddSeconds(42), clock);

        Assert.Null(c.Clock);
        var reading = await c.ProbeClockAsync(default);
        Assert.Equal(HttpMethod.Head, h.Calls.Single().Request.Method);
        Assert.Equal(42.5, reading!.Skew.TotalSeconds, 1);

        await c.GetFundsAsync("R", default);
        Assert.Equal(42.5, c.Clock!.Skew.TotalSeconds, 1);
    }

    [Fact]
    public async Task An_unreachable_paytm_gives_no_clock_reading()
    {
        var h = new FakeHandler((_, _) => throw new HttpRequestException("no route"));
        var c = new PaytmMoneyClient(new HttpClient(h), Options.Create(new PaytmMoneyOptions()), NullLogger<PaytmMoneyClient>.Instance);
        Assert.Null(await c.ProbeClockAsync(default));
    }

    [Fact]
    public async Task Place_sl_order_sends_sdk_fields_and_token_header()
    {
        var (c, h) = Client((_, _) => (HttpStatusCode.OK, """{"status":"success","message":"Order placed","data":[{"order_no":"2410270001"}]}"""));
        var ack = await c.PlaceOrderAsync("ACCESS", new BrokerOrderRequest(TxnType.Sell, 35001, 65, OrderType.StopLossLimit, 83.30m, 85m), default);

        Assert.True(ack.Success);
        Assert.Equal("2410270001", ack.OrderNo);
        var (req, body) = h.Calls.Single();
        Assert.Equal("https://developer.paytmmoney.com/orders/v1/place/regular", req.RequestUri!.ToString());
        Assert.Equal("ACCESS", req.Headers.GetValues("x-jwt-token").Single());
        var json = JsonNode.Parse(body!)!;
        Assert.Equal(("S", "NSE", "D", "I", "35001", 65, "SL", 83.30m, 85m, "DAY"),
            (json["txn_type"]!.GetValue<string>(), json["exchange"]!.GetValue<string>(), json["segment"]!.GetValue<string>(), json["product"]!.GetValue<string>(),
             json["security_id"]!.GetValue<string>(), json["quantity"]!.GetValue<int>(), json["order_type"]!.GetValue<string>(),
             json["price"]!.GetValue<decimal>(), json["trigger_price"]!.GetValue<decimal>(), json["validity"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Modify_includes_order_book_references()
    {
        var (c, h) = Client((_, _) => (HttpStatusCode.OK, """{"status":"success","data":[{"order_no":"1"}]}"""));
        await c.ModifyOrderAsync("A", new BrokerOrderRef("1", "2", "3", "NL"), new BrokerOrderRequest(TxnType.Sell, 1, 65, OrderType.StopLossLimit, 98.2m, 100.25m), default);
        var json = JsonNode.Parse(h.Calls[0].Body!)!;
        Assert.Equal(("1", "2", "3", "NL"), (json["order_no"]!.GetValue<string>(), json["serial_no"]!.GetValue<string>(), json["group_id"]!.GetValue<string>(), json["mkt_type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Rejected_http_status_becomes_failed_ack()
    {
        var (c, _) = Client((_, _) => (HttpStatusCode.BadRequest, """{"status":"error","message":"Insufficient funds"}"""));
        var ack = await c.PlaceOrderAsync("A", new BrokerOrderRequest(TxnType.Buy, 1, 65, OrderType.Limit, 100.5m, null), default);
        Assert.False(ack.Success);
        Assert.Contains("Insufficient", ack.Raw);
    }

    [Fact]
    public async Task Orders_map_statuses_and_fills()
    {
        var (c, _) = Client((_, _) => (HttpStatusCode.OK, """
            {"data":[
              {"order_no":"A","serial_no":1,"group_id":7,"security_id":"35001","txn_type":"B","quantity":65,"traded_qty":65,"avg_traded_price":100.25,"display_status":"Traded"},
              {"order_no":"B","security_id":"35001","txn_type":"S","quantity":65,"traded_qty":0,"display_status":"Trigger Pending"},
              {"order_no":"C","security_id":"35001","txn_type":"S","quantity":130,"traded_qty":65,"display_status":"Pending"},
              {"order_no":"D","security_id":"35001","txn_type":"B","quantity":65,"traded_qty":0,"display_status":"Rejected","reason_description":"RMS: margin"}]}
            """));
        var o = await c.GetOrdersAsync("R", default);
        Assert.Equal([OrderStatus.Filled, OrderStatus.TriggerPending, OrderStatus.PartiallyFilled, OrderStatus.Rejected], o.Select(x => x.Status));
        Assert.Equal((65, 100.25m, TxnType.Buy, "1", "7"), (o[0].FilledQuantity, o[0].AveragePrice!.Value, o[0].Side, o[0].Ref.SerialNo, o[0].Ref.GroupId));
        Assert.Equal("RMS: margin", o[3].Message);
    }

    [Fact]
    public async Task Funds_and_tokens_are_read_tolerantly()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds();
        var jwt = $"h.{Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"exp":{{exp}}}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.s";
        var (c, _) = Client((req, _) => req.RequestUri!.AbsolutePath.Contains("gettoken")
            ? (HttpStatusCode.OK, $$"""{"access_token":"{{jwt}}","public_access_token":"P","read_access_token":"R"}""")
            : (HttpStatusCode.OK, """{"data":{"funds_summary":{"available_cash":"98765.40","utilised_amount":1234.6,"opening_balance":100000}}}"""));

        var tokens = await c.GenerateTokensAsync("k", "s", "req", default);
        Assert.Equal(("P", "R"), (tokens.PublicAccessToken, tokens.ReadAccessToken));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(exp), tokens.ExpiresUtc);

        var f = await c.GetFundsAsync("R", default);
        Assert.Equal((98765.40m, 1234.6m, 100000m), (f.AvailableMargin, f.UtilisedMargin, f.OpeningBalance!.Value));
    }

    [Fact]
    public void Price_chart_accepts_array_and_object_rows()
    {
        using var arrays = JsonDocument.Parse("""{"data":[[1793072700,100,101,99,100.5,1200],[1793073000,100.5,102,100,101.5,900]]}""");
        var a = PaytmMoneyClient.ParseBars(arrays.RootElement);
        Assert.Equal(2, a.Count);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1793072700), a[0].Time);

        using var objects = JsonDocument.Parse("""{"data":{"candles":[{"time":"2026-10-27 09:15:00","open":1,"high":2,"low":0.5,"close":1.5,"volume":10}]}}""");
        var o = Assert.Single(PaytmMoneyClient.ParseBars(objects.RootElement));
        Assert.Equal(new DateTime(2026, 10, 27, 3, 45, 0, DateTimeKind.Utc), o.Time); // naive timestamps are IST
    }

    [Fact]
    public async Task Option_chain_rows_carry_oi()
    {
        var (c, h) = Client((_, _) => (HttpStatusCode.OK, """{"data":{"results":[{"stk_prc":25000,"price":101.2,"oi":125000,"oi_change":5200},{"stk_prc":25050,"price":80,"oi":90000}]}}"""));
        var rows = await c.GetOptionChainAsync("R", "NIFTY", OptionType.Ce, new DateOnly(2026, 11, 3), default);
        Assert.Contains("type=CALL", h.Calls[0].Request.RequestUri!.Query);
        Assert.Contains("expiry=03-11-2026", h.Calls[0].Request.RequestUri!.Query);
        Assert.Equal((25000m, 125000L, 5200L), (rows[0].Strike, rows[0].OpenInterest!.Value, rows[0].OiChange!.Value));
        Assert.Null(rows[1].OiChange);
    }
}

public sealed class SecurityMasterCsvTests
{
    [Fact]
    public void Parses_by_header_aliases_and_normalises_ticks()
    {
        const string csv = """
            security_id,exchange,segment,name,symbol,instrument_type,expiry_date,strike_price,option_type,lot_size,tick_size
            35001,NSE,D,NIFTY,"NIFTY 03 NOV 25000 CE",OPTIDX,03-11-2026,25000,CE,65,5
            35002,NSE,D,NIFTY,NIFTY NOV FUT,FUTIDX,2026-11-24,,XX,65,0.05
            1,NSE,E,RELIANCE,RELIANCE,EQ,,,,1,0.05
            9,NSE,D,X,X,WEIRD,,,,1,0.05
            """;
        var rows = SecurityMasterCsv.Parse(csv).ToList();
        Assert.Equal(3, rows.Count);
        var opt = rows[0];
        Assert.Equal((35001L, "NIFTY", SecurityType.OptIdx, 25000m, OptionType.Ce, new DateOnly(2026, 11, 3), 65, 0.05m),
            (opt.SecurityId, opt.Underlying, opt.Type, opt.Strike!.Value, opt.OptType!.Value, opt.Expiry!.Value, opt.LotSize, opt.TickSize));
        Assert.Equal("NIFTY 03 NOV 25000 CE", opt.Symbol);
        Assert.Equal((SecurityType.FutIdx, (decimal?)null, new DateOnly(2026, 11, 24)), (rows[1].Type, rows[1].Strike, rows[1].Expiry!.Value));
        Assert.Equal(SecurityType.Equity, rows[2].Type);
    }

    [Fact]
    public void Missing_required_columns_fail_loudly() =>
        Assert.Throws<FormatException>(() => SecurityMasterCsv.Parse("a,b\n1,2").ToList());
}

public sealed class GeminiClientTests
{
    private static string Response(string text, params string[] urls) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            ["groundingMetadata"] = new JsonObject { ["groundingChunks"] = new JsonArray(urls.Select(u => (JsonNode)new JsonObject { ["web"] = new JsonObject { ["uri"] = u } }).ToArray()) },
        }),
    }.ToJsonString();

    private const string Items = """{"items":[{"nse_symbol":"HDFCBANK","headline":"HDFC Bank Q2 beats estimates","summary":"NII up 12%","category":"EARNINGS","sentiment":"BULLISH","sentiment_score":72,"impact_level":"HIGH","published_at":"2026-10-27T02:30:00Z","source_urls":["https://example.com/a"]}]}""";

    [Fact]
    public void Request_combines_search_tool_and_schema()
    {
        var r = GeminiNewsClient.Request("p", grounded: true, withSchema: true);
        Assert.NotNull(r["tools"]![0]!["google_search"]);
        Assert.Equal("application/json", r["generationConfig"]!["responseMimeType"]!.GetValue<string>());
        Assert.Contains("EXTREME_HALT", r["generationConfig"]!["responseSchema"]!.ToJsonString());
    }

    [Fact]
    public async Task Single_call_parses_items_and_grounding()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, Response(Items, "https://news.example/1")));
        var result = await new GeminiNewsClient(new HttpClient(h), NullLogger<GeminiNewsClient>.Instance)
            .ScanAsync(new GeminiNewsConfig { ModelName = "gemini-flash-latest" }, "KEY", "scan", default);

        var item = Assert.Single(result.Items);
        Assert.Equal(("HDFCBANK", MarketBias.Bullish, 72, ImpactLevel.High, NewsCategory.Earnings), (item.NseSymbol, item.Sentiment, item.SentimentScore, item.Impact, item.Category));
        Assert.Equal(["https://news.example/1"], result.GroundingUrls);
        Assert.False(result.UsedTwoStepFallback);
        Assert.Equal("KEY", h.Calls[0].Request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task Falls_back_to_two_steps_when_grounding_and_schema_conflict()
    {
        var h = new FakeHandler((_, body) =>
        {
            var hasTools = body!.Contains("google_search");
            var hasSchema = body.Contains("responseSchema");
            return (hasTools, hasSchema) switch
            {
                (true, true) => (HttpStatusCode.BadRequest, """{"error":{"message":"Tool use with a response mime type is unsupported"}}"""),
                (true, false) => (HttpStatusCode.OK, Response("HDFC Bank beat estimates per https://news.example/2", "https://news.example/2")),
                _ => (HttpStatusCode.OK, Response(Items)),
            };
        });
        var result = await new GeminiNewsClient(new HttpClient(h), NullLogger<GeminiNewsClient>.Instance)
            .ScanAsync(new GeminiNewsConfig { ModelName = "m" }, "KEY", "scan", default);

        Assert.True(result.UsedTwoStepFallback);
        Assert.Single(result.Items);
        Assert.Contains("https://news.example/2", result.GroundingUrls);
        Assert.Equal(3, h.Calls.Count);
    }

    [Fact]
    public void Tolerates_fenced_json_and_unknown_enums()
    {
        var r = GeminiNewsClient.Parse(Response("```json\n{\"items\":[{\"headline\":\"x\",\"category\":\"SPORTS\",\"sentiment\":\"bullish\",\"sentiment_score\":500,\"impact_level\":\"high\"}]}\n```"), false);
        var i = Assert.Single(r.Items);
        Assert.Equal((NewsCategory.Other, MarketBias.Bullish, 100, ImpactLevel.High), (i.Category, i.Sentiment, i.SentimentScore, i.Impact));
    }
}

public sealed class TotpAndLoginTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(2000000000, "69279037")]
    public void Rfc6238_sha1_vectors(long unix, string expected) =>
        Assert.Equal(expected, Totp.Compute("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTime.UnixEpoch.AddSeconds(unix), digits: 8));

    [Theory]
    [InlineData("https://myapp.example/callback?success=true&requestToken=abc123&state=x", "abc123")]
    [InlineData("http://localhost:5000/cb?request_token=t%2Fz", "t/z")]
    [InlineData("https://login.paytmmoney.com/merchant-login?apiKey=k", null)]
    public void Request_token_is_extracted_from_redirect(string url, string? expected) =>
        Assert.Equal(expected, PlaywrightBrokerLogin.ExtractRequestToken(url));
}
