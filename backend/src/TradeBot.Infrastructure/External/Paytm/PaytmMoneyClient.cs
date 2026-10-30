using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Application.Market;
using TradeBot.Application.Security;
using TradeBot.Domain.Enums;

namespace TradeBot.Infrastructure.External.Paytm;

/// <summary>
/// Paytm Money Open API over REST. Orders use the access token; reads accept the read token. Failures carry Paytm's
/// result code and are logged with every token, key and secret redacted; each response's Date header keeps a reading of
/// Paytm's clock against ours (TOTP codes fail when the two drift apart).
/// </summary>
public sealed partial class PaytmMoneyClient(HttpClient http, IOptions<PaytmMoneyOptions> options, ILogger<PaytmMoneyClient> log, TimeProvider? clock = null)
    : IPaytmMoneyClient
{
    public const string UserDetailsPath = "/accounts/v1/user/details";
    private readonly PaytmMoneyOptions _o = options.Value;
    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private BrokerClockReading? _clock;

    public BrokerClockReading? Clock => Volatile.Read(ref _clock);

    public string LoginUrl(string apiKey, string state) =>
        $"{_o.LoginUrl}?apiKey={Uri.EscapeDataString(BrokerCredentials.Clean(apiKey) ?? "")}&state={Uri.EscapeDataString(state)}";

    public async Task<BrokerTokens> GenerateTokensAsync(string apiKey, string apiSecret, string requestToken, CancellationToken ct)
    {
        // Values pasted with a trailing space or line break make Paytm answer PM_MERCHANT_AUTH_400607
        var (key, secret, request) = BrokerCredentials.ForTokenRequest(apiKey, apiSecret, requestToken);
        using var doc = await SendAsync(HttpMethod.Post, "/accounts/v2/gettoken", null,
            new { api_key = key, api_secret_key = secret, request_token = request }, ct);
        var root = doc.RootElement;
        if (JsonFind.Str(root, "access_token") is not { Length: > 0 } access)
            throw Failure("/accounts/v2/gettoken", 200, root.GetRawText(), "Paytm token response has no access_token");
        return new BrokerTokens(access, JsonFind.Str(root, "public_access_token"), JsonFind.Str(root, "read_access_token"), JwtExpiry(access));
    }

    public async Task<BrokerProfile> GetProfileAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, UserDetailsPath, token, null, ct);
        var r = doc.RootElement;
        if (IsFailure(r)) throw Failure(UserDetailsPath, 200, r.GetRawText(), "Paytm user details call failed");
        return new BrokerProfile(
            JsonFind.Str(r, "client_id", "user_id", "ucc", "clientcode"),
            JsonFind.Str(r, "name", "user_name", "display_name", "full_name"),
            JsonFind.Str(r, "email", "email_id"),
            JsonFind.Str(r, "mobile", "mobile_no", "phone", "phone_number"),
            r.GetRawText());
    }

    public async Task<BrokerFunds> GetFundsAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/accounts/v1/funds/summary?config=true", token, null, ct);
        var r = doc.RootElement;
        var available = JsonFind.Dec(r, "available_cash", "available_balance", "available_margin", "net_available", "available_funds", "trade_balance");
        if (available is null) log.LogWarning("Funds summary has no recognised available-cash field: {Body}", Truncate(r.GetRawText()));
        return new BrokerFunds(
            available ?? 0,
            JsonFind.Dec(r, "utilised_amount", "utilised_margin", "utilized_amount", "used_margin", "margin_used") ?? 0,
            JsonFind.Dec(r, "opening_balance", "opening_cash", "sod_balance"),
            r.GetRawText());
    }

    public Task<BrokerAck> PlaceOrderAsync(string accessToken, BrokerOrderRequest o, CancellationToken ct) =>
        OrderCallAsync("/orders/v1/place/regular", accessToken, OrderBody(o), ct);

    public Task<BrokerAck> ModifyOrderAsync(string accessToken, BrokerOrderRef reference, BrokerOrderRequest o, CancellationToken ct) =>
        OrderCallAsync("/orders/v1/modify/regular", accessToken, WithRef(OrderBody(o), reference), ct);

    public Task<BrokerAck> CancelOrderAsync(string accessToken, BrokerOrderRef reference, BrokerOrderRequest o, CancellationToken ct) =>
        OrderCallAsync("/orders/v1/cancel/regular", accessToken, WithRef(OrderBody(o), reference), ct);

    public async Task<IReadOnlyList<BrokerOrder>> GetOrdersAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/orders/v1/user/orders", token, null, ct);
        return JsonFind.Rows(doc.RootElement).Where(r => r.ValueKind == JsonValueKind.Object).Select(ParseOrder).ToList();
    }

    public async Task<string> DownloadSecurityMasterAsync(string fileName, CancellationToken ct)
    {
        using var res = await http.GetAsync($"{_o.BaseUrl}/data/v1/scrips/{Uri.EscapeDataString(fileName)}", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw Failure("/data/v1/scrips", (int)res.StatusCode, body, $"Security master download failed ({(int)res.StatusCode})");
        return body;
    }

    public async Task<BrokerClockReading?> ProbeClockAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Head, _o.BaseUrl + "/");
            var sent = _time.GetUtcNow().UtcDateTime;
            using var res = await http.SendAsync(req, cts.Token);
            return RecordClock(res, sent) ? Clock : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogDebug(e, "Paytm clock probe failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<Bar>> GetPriceChartAsync(string token, PriceChartRequest q, CancellationToken ct)
    {
        var body = new
        {
            cont = q.Continuous ? "true" : "false",
            exchange = q.Exchange,
            expiry = q.Expiry?.ToString("yyyy-MM-dd") ?? "",
            fromDate = q.From.ToString("yyyy-MM-dd"),
            instType = DbValue.ToDb(q.InstType),
            interval = _o.PriceChartIntervals.GetValueOrDefault(DbValue.ToDb(q.Interval), DbValue.ToDb(q.Interval)),
            monthId = "",
            series = q.OptType is { } t ? DbValue.ToDb(t) : "XX",
            strike = q.Strike?.ToString(CultureInfo.InvariantCulture) ?? "",
            symbol = q.Symbol,
            toDate = q.To.ToString("yyyy-MM-dd"),
        };
        using var doc = await SendAsync(HttpMethod.Post, "/data/v1/price-charts/sym", token, body, ct);
        var bars = ParseBars(doc.RootElement);
        if (bars.Count == 0) log.LogWarning("Price chart for {Symbol} {Interval} returned no parsable candles: {Body}", q.Symbol, q.Interval, Truncate(doc.RootElement.GetRawText()));
        return bars;
    }

    public async Task<IReadOnlyList<OptionChainRow>> GetOptionChainAsync(string token, string symbol, OptionType type, DateOnly expiry, CancellationToken ct)
    {
        var kind = type == OptionType.Ce ? "CALL" : "PUT";
        using var doc = await SendAsync(HttpMethod.Get, $"/fno/v1/option-chain?type={kind}&symbol={Uri.EscapeDataString(symbol)}&expiry={expiry:dd-MM-yyyy}", token, null, ct);
        return JsonFind.Rows(doc.RootElement)
            .Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => (Row: r, Strike: JsonFind.Dec(r, "strike_price", "stk_prc", "strike")))
            .Where(x => x.Strike is not null)
            .Select(x => new OptionChainRow(
                x.Strike!.Value / _o.StrikeDivisor, type,
                JsonFind.Dec(x.Row, "price", "ltp", "last_price", "last_traded_price"),
                JsonFind.Long(x.Row, "oi", "open_interest"),
                JsonFind.Long(x.Row, "oi_change", "chg_oi", "oi_chg", "change_in_oi")))
            .ToList();
    }

    public async Task<IReadOnlyList<Tick>> GetLiveQuotesAsync(string token, IReadOnlyCollection<FeedSubscription> scrips, CancellationToken ct)
    {
        if (scrips.Count == 0) return [];
        var pref = string.Join(',', scrips.Select(s => $"{s.Exchange}:{s.SecurityId}:{ScripTypeName(s.Type)}"));
        using var doc = await SendAsync(HttpMethod.Get, $"/data/v1/price/live?mode=QUOTE&pref={Uri.EscapeDataString(pref)}", token, null, ct);
        var now = DateTime.UtcNow;
        return JsonFind.Rows(doc.RootElement)
            .Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => (Id: JsonFind.Long(r, "security_id", "scrip_id"), Ltp: JsonFind.Dec(r, "last_price", "ltp"), Vol: JsonFind.Long(r, "volume_traded", "volume"),
                Oi: JsonFind.Long(r, "oi", "open_interest"), Atp: JsonFind.Dec(r, "average_traded_price", "atp", "avg_price")))
            .Where(x => x.Id is not null && x.Ltp is > 0)
            .Select(x => new Tick(x.Id!.Value, x.Ltp!.Value, now, x.Vol, x.Oi, x.Atp))
            .ToList();
    }

    public static string ScripTypeName(ScripType t) => t switch
    {
        ScripType.Index => "INDEX",
        ScripType.Future => "FUTURE",
        ScripType.Option => "OPTION",
        ScripType.Etf => "ETF",
        _ => "EQUITY",
    };

    // ---- helpers ----

    private JsonObject OrderBody(BrokerOrderRequest o)
    {
        var body = new JsonObject
        {
            ["txn_type"] = DbValue.ToDb(o.Side),
            ["exchange"] = o.Exchange,
            ["segment"] = DbValue.ToDb(o.Segment),
            ["product"] = DbValue.ToDb(o.Product),
            ["security_id"] = o.SecurityId.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = o.Quantity,
            ["validity"] = "DAY",
            ["order_type"] = DbValue.ToDb(o.Type),
            ["price"] = o.Price,
            ["source"] = _o.OrderSource,
            ["off_mkt_flag"] = false,
        };
        if (o.TriggerPrice is { } trigger) body["trigger_price"] = trigger;
        return body;
    }

    private static JsonObject WithRef(JsonObject body, BrokerOrderRef r)
    {
        body["order_no"] = r.OrderNo;
        if (r.SerialNo is not null) body["serial_no"] = r.SerialNo;
        if (r.GroupId is not null) body["group_id"] = r.GroupId;
        if (r.MarketType is not null) body["mkt_type"] = r.MarketType;
        return body;
    }

    private async Task<BrokerAck> OrderCallAsync(string path, string token, JsonObject body, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, path, token, body, ct);
            var r = doc.RootElement;
            var status = JsonFind.Str(r, "status");
            var ok = status is null || !(status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase));
            return new BrokerAck(ok, JsonFind.Str(r, "order_no", "orderno"), JsonFind.Str(r, "message", "error_message", "reason"), r.GetRawText());
        }
        catch (BrokerApiException e)
        {
            return new BrokerAck(false, null, e.BrokerMessage ?? e.Message, e.Body);
        }
    }

    internal static BrokerOrder ParseOrder(JsonElement r)
    {
        var qty = (int)(JsonFind.Long(r, "quantity", "qty", "total_qty") ?? 0);
        var filled = (int)(JsonFind.Long(r, "traded_qty", "total_traded_qty", "filled_qty", "tradedqty") ?? 0);
        var side = JsonFind.Str(r, "txn_type", "transaction_type")?.StartsWith('S') == true ? TxnType.Sell : TxnType.Buy;
        return new BrokerOrder(
            new BrokerOrderRef(JsonFind.Str(r, "order_no", "orderno") ?? "", JsonFind.Str(r, "serial_no"), JsonFind.Str(r, "group_id"), JsonFind.Str(r, "mkt_type", "market_type")),
            JsonFind.Long(r, "security_id") ?? 0, side, qty, filled,
            JsonFind.Dec(r, "avg_traded_price", "average_price", "avg_price", "traded_price"),
            MapStatus(JsonFind.Str(r, "display_status", "status", "order_status"), qty, filled),
            JsonFind.Str(r, "reason_description", "error_message", "rejection_reason", "message"),
            r.GetRawText());
    }

    internal static OrderStatus MapStatus(string? status, int qty, int filled)
    {
        var s = (status ?? "").ToLowerInvariant();
        if (s.Contains("reject")) return OrderStatus.Rejected;
        if (s.Contains("cancel")) return OrderStatus.Cancelled;
        if (s.Contains("trigger")) return OrderStatus.TriggerPending;
        if (s.Contains("traded") || s.Contains("execut") || s.Contains("complete") || s.Contains("filled"))
            return qty > 0 && filled < qty ? OrderStatus.PartiallyFilled : OrderStatus.Filled;
        if (s.Contains("partial")) return OrderStatus.PartiallyFilled;
        if (s.Contains("pending") || s.Contains("open") || s.Contains("transit") || s.Contains("modif") || s.Contains("received"))
            return filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Open;
        return OrderStatus.Placed;
    }

    internal static List<Bar> ParseBars(JsonElement root)
    {
        var bars = new List<Bar>();
        foreach (var row in JsonFind.Rows(root))
        {
            if (row.ValueKind == JsonValueKind.Array)
            {
                var a = row.EnumerateArray().ToArray();
                if (a.Length < 5 || ParseTime(a[0]) is not { } t) continue;
                bars.Add(new Bar(t, Num(a[1]), Num(a[2]), Num(a[3]), Num(a[4]), a.Length > 5 ? (long)Num(a[5]) : 0, a.Length > 6 ? (long)Num(a[6]) : null));
            }
            else if (row.ValueKind == JsonValueKind.Object
                     && JsonFind.Find(row, "time", "timestamp", "datetime", "date", "t") is { } tv && ParseTime(tv) is { } t2
                     && JsonFind.Dec(row, "open", "o") is { } o && JsonFind.Dec(row, "high", "h") is { } h
                     && JsonFind.Dec(row, "low", "l") is { } l && JsonFind.Dec(row, "close", "c") is { } c)
            {
                bars.Add(new Bar(t2, o, h, l, c, JsonFind.Long(row, "volume", "v") ?? 0, JsonFind.Long(row, "oi", "open_interest")));
            }
        }
        return bars.OrderBy(b => b.Time).ToList();

        static decimal Num(JsonElement e) => JsonFind.ToDecimal(e) ?? 0;
    }

    private static DateTime? ParseTime(JsonElement e)
    {
        if (JsonFind.ToDecimal(e) is { } n && e.ValueKind == JsonValueKind.Number)
            return (n > 10_000_000_000 ? DateTime.UnixEpoch.AddMilliseconds((double)n) : DateTime.UnixEpoch.AddSeconds((double)n));
        if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto))
        {
            // Naive timestamps from Indian venues are IST
            var s = e.GetString()!;
            var hasZone = s.EndsWith('Z') || s.Contains('+') || s.LastIndexOf('-') > 9;
            return hasZone ? dto.UtcDateTime : DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Utc).AddMinutes(-330);
        }
        return null;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _o.BaseUrl + path);
        req.Headers.Add("openapi-client-src", "sdk");
        if (token is not null) req.Headers.Add("x-jwt-token", token);
        if (body is not null) req.Content = body is JsonObject jo
            ? new StringContent(jo.ToJsonString(), Encoding.UTF8, "application/json")
            : JsonContent.Create(body);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_o.RequestTimeoutSeconds));
        var sent = _time.GetUtcNow().UtcDateTime;
        using var res = await http.SendAsync(req, cts.Token);
        RecordClock(res, sent);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode) throw Failure(path, (int)res.StatusCode, text, $"Paytm API {Queryless(path)} returned {(int)res.StatusCode}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>
    /// Builds the exception for a refused call and logs it once, clearly: path, HTTP status, Paytm's result code and
    /// message, and what to do. The body is redacted before it is logged or kept.
    /// </summary>
    private BrokerApiException Failure(string path, int status, string body, string summary)
    {
        var (code, message) = ErrorOf(body);
        var redacted = SecretRedactor.Redact(Truncate(body));
        var e = new BrokerApiException(code is null ? $"{summary}." : $"{summary}: {code}.", status, code, message, redacted);
        var auth = path.StartsWith("/accounts/", StringComparison.Ordinal) || e.TokenRejected;
        log.Log(auth ? LogLevel.Error : LogLevel.Warning, "Paytm {Kind} failed on {Path} (HTTP {Status}): {Error}",
            auth ? "authentication" : "call", Queryless(path), status, BrokerErrors.Describe(e));
        log.LogDebug("Paytm {Path} response body (redacted): {Body}", Queryless(path), redacted);
        return e;
    }

    /// <summary>Paytm's result code (PM_MERCHANT_AUTH_400607 …) and message from an error body, when it has them.</summary>
    internal static (string? Code, string? Message) ErrorOf(string body)
    {
        string? code = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                code = JsonFind.Str(r, "error_code", "response_code", "result_code", "code");
                message = JsonFind.Str(r, "message", "error_message", "msg", "result_msg", "description");
            }
        }
        catch (JsonException) { message = body.TrimStart().StartsWith('<') ? null : body.Length > 200 ? body[..200] : body; }
        // The PM_* code is what Paytm's docs list; prefer it wherever it appears in the body
        if (BrokerCode().Match(body) is { Success: true } m) code = m.Value;
        return (string.IsNullOrWhiteSpace(code) ? null : code.Trim(), string.IsNullOrWhiteSpace(message) ? null : SecretRedactor.Redact(message.Trim()));
    }

    private static bool IsFailure(JsonElement r) =>
        JsonFind.Str(r, "status") is { } status && (status.Equals("failure", StringComparison.OrdinalIgnoreCase) || status.Equals("error", StringComparison.OrdinalIgnoreCase));

    /// <summary>Date has whole seconds and cached responses carry Age; compare with the middle of our round trip.</summary>
    private bool RecordClock(HttpResponseMessage res, DateTime sentUtc)
    {
        if (res.Headers.Date is not { } date) return false;
        var received = _time.GetUtcNow().UtcDateTime;
        var server = date.UtcDateTime + (res.Headers.Age ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(500);
        var local = sentUtc + (received - sentUtc) / 2;
        Volatile.Write(ref _clock, new BrokerClockReading(server - local, received));
        return true;
    }

    private static string Queryless(string path) => path.Split('?')[0];

    [GeneratedRegex(@"PM_[A-Z]+(?:_[A-Z]+)*_\d{3,}")]
    private static partial Regex BrokerCode();

    internal static DateTime? JwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var s) ? DateTime.UnixEpoch.AddSeconds(s) : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string s) => s.Length <= 2000 ? s : s[..2000];
}
