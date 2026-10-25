using System.Globalization;
using System.Text.Json;

namespace TradeBot.Infrastructure.External;

/// <summary>
/// Tolerant lookups for broker/AI payloads whose exact shape is not contractually fixed:
/// names match case-insensitively ignoring underscores, searching the object before its children.
/// </summary>
internal static class JsonFind
{
    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    public static JsonElement? Find(JsonElement e, params string[] names)
    {
        var wanted = names.Select(Norm).ToArray();
        return Search(e, wanted, 0);
    }

    private static JsonElement? Search(JsonElement e, string[] wanted, int depth)
    {
        if (depth > 8) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var w in wanted)
                foreach (var p in e.EnumerateObject())
                    if (Norm(p.Name) == w && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return p.Value;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && Search(p.Value, wanted, depth + 1) is { } hit) return hit;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (Search(item, wanted, depth + 1) is { } hit) return hit;
        }
        return null;
    }

    public static string? Str(JsonElement e, params string[] names) => Find(e, names) is { } v
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
        : null;

    public static decimal? Dec(JsonElement e, params string[] names) => Find(e, names) is { } v ? ToDecimal(v) : null;

    public static long? Long(JsonElement e, params string[] names) => Dec(e, names) is { } d ? (long)d : null;

    public static decimal? ToDecimal(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>First array of objects in the payload (prefers a "data" member).</summary>
    public static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().ToList();
            if (data.ValueKind == JsonValueKind.Object) { var inner = Rows(data).ToList(); return inner.Count > 0 ? inner : [data]; }
        }
        foreach (var p in root.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any()) return p.Value.EnumerateArray().ToList();
        foreach (var p in root.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && Rows(p.Value).ToList() is { Count: > 0 } nested) return nested;
        return [];
    }
}
