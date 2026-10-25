using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TradeBot.Application.Validation.Forms;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Infrastructure.Tests;

/// <summary>
/// Proves the fixed form bounds equal the DB CHECKs: every shared vector the validator accepts must insert,
/// and every one it rejects must be refused by PostgreSQL. Codes the DB cannot express (type, scale, pattern) are skipped.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class FormDbParityTests(PostgresFixture fx)
{
    private static readonly JsonObject Forms = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "validation-vectors.json")))!["forms"]!.AsObject();
    private static readonly string[] NotDbExpressible = ["FIELD_TYPE", "FIELD_SCALE", "FIELD_REQUIRED", "FIELD_ENUM", "FIELD_PATTERN", "FIELD_MAX_LENGTH"];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var form in new[] { FormRuleCatalog.RiskConfig, FormRuleCatalog.StrategyConfig, FormRuleCatalog.GeminiConfig, FormRuleCatalog.MarketHoliday })
            foreach (var c in Forms[form]!["cases"]!.AsArray())
                if (!c!["expect"]!.AsArray().Any(e => NotDbExpressible.Any(e!.GetValue<string>().EndsWith)))
                    data.Add(form, c["name"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Validator_and_database_agree(string form, string name)
    {
        var spec = Forms[form]!;
        var testCase = spec["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;
        var values = spec["base"]!.DeepClone().AsObject();
        foreach (var (key, value) in testCase["patch"]!.AsObject()) values[key] = value?.DeepClone();

        var validatorAccepts = FormValidator.Validate(form, values).Count == 0;
        Assert.Equal(testCase["expect"]!.AsArray().Count == 0, validatorAccepts);

        await using var db = fx.CreateContext();
        db.Add(ToEntity(form, NormalizeTimes(values)));
        var dbAccepts = true;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { dbAccepts = false; }

        Assert.Equal(validatorAccepts, dbAccepts);
    }

    private static object ToEntity(string form, JsonObject values)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        switch (form)
        {
            case FormRuleCatalog.RiskConfig:
                return new TradingAccount { AccountName = suffix, ClientId = suffix, UserName = "u", RiskConfig = Deserialize<AccountRiskConfig>(values) };
            case FormRuleCatalog.StrategyConfig:
                var strategy = Deserialize<StrategyConfig>(values);
                strategy.IsActive = false; // single-active unique index
                return strategy;
            case FormRuleCatalog.GeminiConfig:
                var gemini = Deserialize<GeminiNewsConfig>(values);
                gemini.IsActive = false;
                return gemini;
            default:
                var holiday = Deserialize<MarketHoliday>(values);
                holiday.HolidayDate = holiday.HolidayDate.AddDays(Random.Shared.Next(1, 3000)); // unique per exchange/date
                return holiday;
        }
    }

    private static T Deserialize<T>(JsonObject o) => o.Deserialize<T>(FormValidator.EntityJson)!;

    private static JsonObject NormalizeTimes(JsonObject o)
    {
        foreach (var (key, value) in o.ToList())
            if (value?.GetValueKind() == JsonValueKind.String && ShortTime().IsMatch(value.GetValue<string>()))
                o[key] = value.GetValue<string>() + ":00";
        return o;
    }

    [GeneratedRegex("^[0-9]{2}:[0-9]{2}$")]
    private static partial Regex ShortTime();
}
