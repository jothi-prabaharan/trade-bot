using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TradeBot.Application.Serialization;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Validation.Forms;

/// <summary>Evaluates a <see cref="FormRuleSet"/> against camelCase JSON, exactly as the Angular forms do.</summary>
public static class FormValidator
{
    private static readonly string[] TimeFormats = ["HH:mm:ss", "HH:mm"];
    private static readonly TimeOnly SessionLastMinute = new(15, 29);

    public static readonly JsonSerializerOptions EntityJson = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new DbValueEnumJsonConverterFactory() },
    };

    public static List<RuleViolation> ValidateEntity<T>(string form, T entity) =>
        Validate(form, JsonSerializer.SerializeToNode(entity, EntityJson)!.AsObject());

    public static List<RuleViolation> Validate(string form, JsonObject values)
    {
        var set = FormRuleCatalog.Forms[form];
        var violations = new List<RuleViolation>();
        foreach (var rule in set.Fields)
            if (CheckField(rule, values[rule.Field]) is { } code)
                violations.Add(new RuleViolation(code, FieldMessage(code, rule), rule.Field));

        var badFields = violations.Select(x => x.Field).ToHashSet();
        foreach (var cross in set.CrossFieldRules)
            if (!cross.Fields.Any(badFields.Contains) && !CrossHolds(cross.Code, values))
                violations.Add(new RuleViolation(cross.Code, cross.Message));
        return violations;
    }

    private static string? CheckField(FieldRule rule, JsonNode? node)
    {
        if (IsEmpty(node)) return rule.Required ? FormCodes.Required : null;
        if (node is not JsonValue value) return FormCodes.Type;

        switch (rule.Type)
        {
            case FieldType.Number or FieldType.Integer:
                if (value.GetValueKind() != JsonValueKind.Number) return FormCodes.Type;
                var d = value.GetValue<decimal>();
                if (rule.Type == FieldType.Integer && d != Math.Truncate(d)) return FormCodes.Type;
                if (rule.Min is { } min && (rule.MinExclusive ? d <= min : d < min)) return FormCodes.Min;
                if (rule.Max is { } max && (rule.MaxExclusive ? d >= max : d > max)) return FormCodes.Max;
                if (rule.Scale is { } scale && d * Pow10(scale) % 1 != 0) return FormCodes.Scale;
                return null;
            case FieldType.Boolean:
                return value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : FormCodes.Type;
        }

        if (value.GetValueKind() != JsonValueKind.String) return FormCodes.Type;
        var s = value.GetValue<string>();
        return rule.Type switch
        {
            FieldType.String when s.Length > rule.MaxLength => FormCodes.MaxLength,
            FieldType.String when rule.Pattern is { } p && !Regex.IsMatch(s, p, RegexOptions.CultureInvariant) => FormCodes.Pattern,
            FieldType.Time when ParseTime(s) is null => FormCodes.Type,
            FieldType.Date when !DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => FormCodes.Type,
            FieldType.Enum when !rule.AllowedValues!.Contains(s) => FormCodes.Enum,
            FieldType.Json when !IsJsonObject(s) => FormCodes.Type,
            _ => null,
        };
    }

    private static bool CrossHolds(string code, JsonObject o) => code switch
    {
        FormCodes.PremiumBand => Dec(o, "maxSingleTradePrice") > Dec(o, "minSingleTradePrice"),
        FormCodes.MaxSlGteDefaultSl => Dec(o, "maxSlPct") >= Dec(o, "defaultSlPct"),
        FormCodes.FallbackRr => Dec(o, "defaultTargetPct") >= Dec(o, "defaultSlPct") * Dec(o, "minRiskRewardRatio"),
        FormCodes.DailyLossLteCapital => Dec(o, "maxDailyLoss") <= Dec(o, "allocatedCapital"),
        FormCodes.DataProviderCredentials => !(Bool(o, "isDataProvider") && Bool(o, "isActive"))
            || ((HasText(o, "apiKey") || Bool(o, "hasApiKey")) && (HasText(o, "apiSecret") || Bool(o, "hasApiSecret"))),
        FormCodes.WeightsSum100 => Dec(o, "patternWeightPct") + Dec(o, "newsWeightPct") + Dec(o, "oiGreeksWeightPct") == 100,
        FormCodes.PremarketBeforeOpen => Time(o, "premarketScanTime") < FixedLimits.MarketOpen,
        FormCodes.SessionTimes => Time(o, "entryStartTime") >= FixedLimits.MarketOpen
            && Time(o, "entryCutoffTime") > Time(o, "entryStartTime")
            && Time(o, "squareOffTime") > Time(o, "entryCutoffTime")
            && Time(o, "squareOffTime") <= SessionLastMinute,
        FormCodes.AuthBeforeEntry => Time(o, "authLoginTime") < Time(o, "entryStartTime"),
        FormCodes.TrendAboveTrigger => TimeframeRank(o, "trendTimeframe") > TimeframeRank(o, "triggerTimeframe"),
        FormCodes.RealConfirmation => Text(o, "targetMode") != DbValue.ToDb(TradeMode.Real)
            || Text(o, "confirmClientId")?.Trim() == Text(o, "accountClientId"),
        FormCodes.SpecialSessionTimes => Bool(o, "isSpecialSession")
            ? !IsEmpty(o["sessionOpenTime"]) && !IsEmpty(o["sessionCloseTime"]) && Time(o, "sessionOpenTime") < Time(o, "sessionCloseTime")
            : IsEmpty(o["sessionOpenTime"]) && IsEmpty(o["sessionCloseTime"]),
        _ => throw new InvalidOperationException($"No implementation for cross-field rule {code}"),
    };

    private static bool IsEmpty(JsonNode? n) => n is null || (n.GetValueKind() == JsonValueKind.String && n.GetValue<string>().Length == 0);
    private static decimal Dec(JsonObject o, string f) => o[f]!.GetValue<decimal>();
    private static bool Bool(JsonObject o, string f) => o[f]?.GetValueKind() == JsonValueKind.True;
    private static string? Text(JsonObject o, string f) => o[f]?.GetValueKind() == JsonValueKind.String ? o[f]!.GetValue<string>() : null;
    private static bool HasText(JsonObject o, string f) => !string.IsNullOrEmpty(Text(o, f));
    private static TimeOnly Time(JsonObject o, string f) => ParseTime(o[f]!.GetValue<string>())!.Value;
    private static int TimeframeRank(JsonObject o, string f) => DbValue.AllValues(typeof(Timeframe)).ToList().IndexOf(Text(o, f)!);

    private static TimeOnly? ParseTime(string s) =>
        TimeOnly.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static bool IsJsonObject(string s)
    {
        try { return JsonNode.Parse(s) is JsonObject; }
        catch (JsonException) { return false; }
    }

    private static decimal Pow10(int scale) => (decimal)Math.Pow(10, scale);

    private static string FieldMessage(string code, FieldRule r) => code switch
    {
        FormCodes.Required => "This field is required.",
        FormCodes.Type => $"Must be a valid {r.Type.ToString().ToLowerInvariant()}.",
        FormCodes.Min => $"Must be {(r.MinExclusive ? "greater than" : "at least")} {r.Min}.",
        FormCodes.Max => $"Must be {(r.MaxExclusive ? "less than" : "at most")} {r.Max}.",
        FormCodes.Scale => $"At most {r.Scale} decimal places.",
        FormCodes.MaxLength => $"At most {r.MaxLength} characters.",
        FormCodes.Pattern => "Invalid format.",
        FormCodes.Enum => $"Must be one of: {string.Join(", ", r.AllowedValues!)}.",
        _ => code,
    };
}
