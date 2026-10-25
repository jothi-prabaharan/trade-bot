using System.Text.Json;
using System.Text.Json.Serialization;
using TradeBot.Application.Validation.Forms;

namespace TradeBot.Application.Validation;

/// <summary>Payload of GET /api/validation/rules: everything Angular needs to mirror backend validation.</summary>
public sealed record ValidationRulesDocument(
    string Version,
    IReadOnlyCollection<RuleInfo> Rules,
    IReadOnlyCollection<FormRuleSet> Forms,
    IReadOnlyDictionary<string, string> FixedLimits)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ValidationRulesDocument Build() => new(
        Version: "2",
        Rules: RuleCatalog.All,
        Forms: FormRuleCatalog.Forms.Values.ToList(),
        FixedLimits: new Dictionary<string, string>
        {
            ["marketOpen"] = Validation.FixedLimits.MarketOpen.ToString("HH:mm:ss"),
            ["marketCloseExclusive"] = Validation.FixedLimits.MarketCloseExclusive.ToString("HH:mm:ss"),
            ["maxTickAgeSeconds"] = Validation.FixedLimits.MaxTickAge.TotalSeconds.ToString("0"),
            ["tokenExpiryBufferMinutes"] = Validation.FixedLimits.TokenExpiryBuffer.TotalMinutes.ToString("0"),
            ["newsVetoMinConstituentWeightPct"] = Validation.FixedLimits.NewsVetoMinConstituentWeightPct.ToString("0.##"),
        });
}
