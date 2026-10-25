using System.Text.Json.Nodes;
using TradeBot.Application.Validation;
using TradeBot.Application.Validation.Forms;

namespace TradeBot.Application.Tests;

/// <summary>Runs shared/validation/validation-vectors.json; the Angular suite runs the same file.</summary>
public sealed class SharedVectorTests
{
    private static readonly JsonObject Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "validation-vectors.json")))!.AsObject();

    public static TheoryData<string, string> FormCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (form, node) in Vectors["forms"]!.AsObject())
            foreach (var c in node!["cases"]!.AsArray())
                data.Add(form, c!["name"]!.GetValue<string>());
        return data;
    }

    public static TheoryData<string> BracketCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors["bracket"]!.AsArray()) data.Add(c!["name"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(FormCases))]
    public void Form_vector(string form, string name)
    {
        var spec = Vectors["forms"]![form]!;
        var testCase = spec["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;
        var values = spec["base"]!.DeepClone().AsObject();
        foreach (var (key, value) in testCase["patch"]!.AsObject()) values[key] = value?.DeepClone();

        var actual = FormValidator.Validate(form, values).Select(v => v.ToString()).Order();
        Assert.Equal(Expected(testCase), actual);
    }

    [Theory]
    [MemberData(nameof(BracketCases))]
    public void Bracket_vector(string name)
    {
        var c = Vectors["bracket"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var actual = BracketRules.Validate(c["entry"]!.GetValue<decimal>(), c["stopLoss"]?.GetValue<decimal>(), c["target"]?.GetValue<decimal>())
            .Select(v => v.ToString()).Order();
        Assert.Equal(Expected(c), actual);
    }

    [Fact]
    public void Every_form_and_cross_rule_is_covered_by_a_failing_vector()
    {
        var expectedCodes = Vectors["forms"]!.AsObject()
            .SelectMany(f => f.Value!["cases"]!.AsArray())
            .SelectMany(c => c!["expect"]!.AsArray().Select(e => e!.GetValue<string>()))
            .ToHashSet();
        var crossCodes = FormRuleCatalog.Forms.Values.SelectMany(f => f.CrossFieldRules).Select(r => r.Code);
        Assert.All(crossCodes, code => Assert.Contains(code, expectedCodes));
        Assert.Equal(FormRuleCatalog.Forms.Keys.Order(), Vectors["forms"]!.AsObject().Select(f => f.Key).Order());
    }

    private static IEnumerable<string> Expected(JsonNode c) => c["expect"]!.AsArray().Select(e => e!.GetValue<string>()).Order();
}
