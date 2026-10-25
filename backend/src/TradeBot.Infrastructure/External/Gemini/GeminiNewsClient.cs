using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Infrastructure.External.Gemini;

/// <summary>
/// Gemini generateContent with Google Search grounding and a JSON response schema. Models that reject
/// grounding + schema together get a two-step call: grounded research text, then schema-only structuring.
/// </summary>
public sealed partial class GeminiNewsClient(HttpClient http, ILogger<GeminiNewsClient> log) : INewsIntelligenceClient
{
    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public async Task<AiNewsResult> ScanAsync(GeminiNewsConfig config, string apiKey, string prompt, CancellationToken ct)
    {
        var grounded = config.UseSearchGrounding;
        var (status, body) = await GenerateAsync(config.ModelName, apiKey, Request(prompt, grounded, withSchema: true), ct);
        if (status == HttpStatusCode.OK) return Parse(body, usedFallback: false);

        if (grounded && status == HttpStatusCode.BadRequest)
        {
            log.LogInformation("Model {Model} rejected grounding + schema in one call; using two-step fallback", config.ModelName);
            var (s1, research) = await GenerateAsync(config.ModelName, apiKey, Request(prompt + "\n\nReport findings as plain text with source URLs.", true, withSchema: false), ct);
            if (s1 != HttpStatusCode.OK) throw new HttpRequestException($"Gemini grounded call failed: {(int)s1} {Truncate(research)}");
            var notes = Text(research);
            var urls = GroundingUrls(research);
            var (s2, structured) = await GenerateAsync(config.ModelName, apiKey,
                Request($"Convert these market news research notes into the JSON schema. Keep only facts present in the notes.\n\n{notes}", false, withSchema: true), ct);
            if (s2 != HttpStatusCode.OK) throw new HttpRequestException($"Gemini structuring call failed: {(int)s2} {Truncate(structured)}");
            var result = Parse(structured, usedFallback: true);
            return result with { GroundingUrls = urls.Concat(result.GroundingUrls).Distinct().ToList() };
        }
        throw new HttpRequestException($"Gemini call failed: {(int)status} {Truncate(body)}");
    }

    private async Task<(HttpStatusCode Status, string Body)> GenerateAsync(string model, string apiKey, JsonObject request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{Uri.EscapeDataString(model)}:generateContent")
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-goog-api-key", apiKey);
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError;
            if (!retryable || attempt >= 2) return (res.StatusCode, body);
            await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
        }
    }

    internal static JsonObject Request(string prompt, bool grounded, bool withSchema)
    {
        var req = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2 },
        };
        if (grounded) req["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });
        if (withSchema)
        {
            req["generationConfig"]!["responseMimeType"] = "application/json";
            req["generationConfig"]!["responseSchema"] = Schema();
        }
        return req;
    }

    internal static JsonObject Schema()
    {
        static JsonArray Enum<T>() where T : struct, System.Enum => new(DbValue.AllValues(typeof(T)).Select(v => (JsonNode)v).ToArray());
        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JsonObject
                        {
                            ["nse_symbol"] = new JsonObject { ["type"] = "STRING", ["nullable"] = true },
                            ["index_symbol"] = new JsonObject { ["type"] = "STRING", ["nullable"] = true },
                            ["headline"] = new JsonObject { ["type"] = "STRING" },
                            ["summary"] = new JsonObject { ["type"] = "STRING" },
                            ["category"] = new JsonObject { ["type"] = "STRING", ["enum"] = Enum<NewsCategory>() },
                            ["sentiment"] = new JsonObject { ["type"] = "STRING", ["enum"] = Enum<MarketBias>() },
                            ["sentiment_score"] = new JsonObject { ["type"] = "INTEGER", ["description"] = "-100 (very bearish) to +100 (very bullish)" },
                            ["impact_level"] = new JsonObject { ["type"] = "STRING", ["enum"] = Enum<ImpactLevel>() },
                            ["published_at"] = new JsonObject { ["type"] = "STRING", ["nullable"] = true, ["description"] = "ISO-8601 timestamp" },
                            ["source_urls"] = new JsonObject { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "STRING" } },
                        },
                        ["required"] = new JsonArray("headline", "category", "sentiment", "sentiment_score", "impact_level"),
                    },
                },
            },
            ["required"] = new JsonArray("items"),
        };
    }

    internal static AiNewsResult Parse(string responseBody, bool usedFallback)
    {
        var text = Text(responseBody);
        var json = ExtractJson(text);
        var items = new List<AiNewsItem>();
        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("items", out var it) ? it : default;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (ToItem(e) is { } item) items.Add(item);
        }
        return new AiNewsResult(items, GroundingUrls(responseBody), responseBody, usedFallback);
    }

    private static AiNewsItem? ToItem(JsonElement e)
    {
        if (JsonFind.Str(e, "headline") is not { Length: > 0 } headline) return null;
        static T Enum<T>(string? v, T fallback) where T : struct, System.Enum
        {
            try { return v is null ? fallback : DbValue.FromDb<T>(v.Trim().ToUpperInvariant()); }
            catch (ArgumentOutOfRangeException) { return fallback; }
        }
        var score = (int)Math.Clamp(JsonFind.Dec(e, "sentiment_score") ?? 0, -100, 100);
        var published = JsonFind.Str(e, "published_at") is { } p && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) ? dto.UtcDateTime : (DateTime?)null;
        var urls = JsonFind.Find(e, "source_urls") is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList()
            : [];
        return new AiNewsItem(
            Blank(JsonFind.Str(e, "nse_symbol"))?.ToUpperInvariant(), Blank(JsonFind.Str(e, "index_symbol"))?.ToUpperInvariant(),
            headline.Length > 500 ? headline[..500] : headline, JsonFind.Str(e, "summary"),
            Enum(JsonFind.Str(e, "category"), NewsCategory.Other), Enum(JsonFind.Str(e, "sentiment"), MarketBias.Neutral),
            score, Enum(JsonFind.Str(e, "impact_level"), ImpactLevel.Low), published, urls);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();

    internal static string Text(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("candidates", out var c) || c.GetArrayLength() == 0) return "";
        var sb = new StringBuilder();
        if (c[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        return sb.ToString();
    }

    internal static List<string> GroundingUrls(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var urls = new List<string>();
        if (doc.RootElement.TryGetProperty("candidates", out var c) && c.GetArrayLength() > 0
            && c[0].TryGetProperty("groundingMetadata", out var gm) && gm.TryGetProperty("groundingChunks", out var chunks))
            foreach (var ch in chunks.EnumerateArray())
                if (ch.TryGetProperty("web", out var web) && web.TryGetProperty("uri", out var uri)) urls.Add(uri.GetString()!);
        return urls;
    }

    private static string? ExtractJson(string text)
    {
        var fenced = FencedJson().Match(text);
        var candidate = fenced.Success ? fenced.Groups[1].Value : text;
        var start = candidate.IndexOfAny(['{', '[']);
        var end = Math.Max(candidate.LastIndexOf('}'), candidate.LastIndexOf(']'));
        if (start < 0 || end <= start) return null;
        var json = candidate[start..(end + 1)];
        try { using var _ = JsonDocument.Parse(json); return json; }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```")]
    private static partial Regex FencedJson();

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];
}
