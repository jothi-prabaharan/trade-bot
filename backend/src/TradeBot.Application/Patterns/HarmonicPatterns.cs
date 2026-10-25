using System.Text.Json.Nodes;
using TradeBot.Domain.Enums;
using static TradeBot.Domain.Enums.PatternDirection;

namespace TradeBot.Application.Patterns;

/// <summary>Fibonacci ratio windows; null bounds mean "not checked" for that leg.</summary>
internal sealed record HarmonicSpec(
    string Name, decimal Weight,
    (decimal Lo, decimal Hi)? AbXa, (decimal Lo, decimal Hi)? BcAb, (decimal Lo, decimal Hi)? CdBc, (decimal Lo, decimal Hi)? AdXa,
    bool Cypher = false);

/// <summary>
/// XABCD harmonics on zigzag pivots: X,A,B,C are confirmed pivots, D is the current bar's fresh extreme.
/// Entry needs a close beyond the D bar; SL sits <c>slXaFrac</c> of XA past D; target is <c>targetFib</c> of AD.
/// </summary>
internal static class HarmonicCatalog
{
    private static readonly HarmonicSpec[] Specs =
    [
        new("GARTLEY", 70, (0.618m, 0.618m), (0.382m, 0.886m), (1.272m, 1.618m), (0.786m, 0.786m)),
        new("BAT", 70, (0.382m, 0.5m), (0.382m, 0.886m), (1.618m, 2.618m), (0.886m, 0.886m)),
        new("BUTTERFLY", 68, (0.786m, 0.786m), (0.382m, 0.886m), (1.618m, 2.24m), (1.27m, 1.618m)),
        new("CRAB", 68, (0.382m, 0.618m), (0.382m, 0.886m), (2.24m, 3.618m), (1.618m, 1.618m)),
        new("CYPHER", 66, (0.382m, 0.618m), null, null, null, Cypher: true),
        new("ABCD", 62, null, (0.382m, 0.886m), (1.13m, 2.618m), null),
    ];

    private static JsonObject Defaults() => new()
    {
        ["zigzagAtr"] = 1.2m, ["fibTol"] = 0.05m, ["slXaFrac"] = 0.13m, ["targetFib"] = 0.618m,
        ["confirmWithinBars"] = 5m, ["quality"] = 0.85m,
    };

    public static IReadOnlyList<IPatternDetector> All { get; } = Specs.SelectMany(s => new IPatternDetector[]
    {
        Make(s, Bullish),
        Make(s, Bearish),
    }).ToList();

    private static IPatternDetector Make(HarmonicSpec s, PatternDirection dir)
    {
        var code = $"HRM_{s.Name}_{(dir == Bullish ? "BULL" : "BEAR")}";
        var name = $"{(dir == Bullish ? "Bullish" : "Bearish")} {char.ToUpperInvariant(s.Name[0])}{s.Name[1..].ToLowerInvariant()}";
        var def = new PatternDefinition(code, name, PatternCategory.Harmonic, dir, 40, s.Weight, false, true, Defaults());
        return new FuncDetector(def, c => Detect(c, s, code, dir));
    }

    private static IEnumerable<PatternSetup> Detect(PatternContext c, HarmonicSpec s, string code, PatternDirection dir)
    {
        var bull = dir == Bullish;
        var key = $"zz:{c.P["zigzagAtr"]}";
        if (!c.Cache.TryGetValue(key, out var cached)) c.Cache[key] = cached = PivotFinder.ZigZag(c.Bars, c.Atr, c.P["zigzagAtr"]);
        if (PivotFinder.Tail((List<Pivot>)cached, bull ? "LHLH" : "HLHL") is not { } t) yield break;
        var (x, a, b, cc) = (t[0], t[1], t[2], t[3]);
        var i = c.Last;
        if (i <= cc.Index) yield break;

        // D must be a fresh extreme of the leg out of C
        var d = bull ? c.Bars[i].Low : c.Bars[i].High;
        for (var j = cc.Index + 1; j < i; j++)
            if (bull ? c.Bars[j].Low <= d : c.Bars[j].High >= d) yield break;

        var xa = Math.Abs(a.Price - x.Price);
        var ab = Math.Abs(a.Price - b.Price);
        var bc = Math.Abs(cc.Price - b.Price);
        var cd = Math.Abs(cc.Price - d);
        if (xa == 0 || ab == 0 || bc == 0) yield break;
        var tol = c.P["fibTol"];
        var deviations = new List<decimal>();

        bool Within((decimal Lo, decimal Hi)? w, decimal v)
        {
            if (w is not { } r) return true;
            var ok = v >= r.Lo * (1 - tol) && v <= r.Hi * (1 + tol);
            var center = (r.Lo + r.Hi) / 2;
            deviations.Add(Math.Min(1, Math.Abs(v - center) / Math.Max((r.Hi - r.Lo) / 2 + center * tol, 1e-9m)));
            return ok;
        }

        if (s.Cypher)
        {
            // C extends beyond A by 1.272-1.414 of XA; D retraces 0.786 of XC
            var xc = Math.Abs(cc.Price - x.Price);
            var beyondA = bull ? cc.Price > a.Price : cc.Price < a.Price;
            if (!beyondA || !Within(s.AbXa, ab / xa) || !Within((1.272m, 1.414m), xc / xa) || !Within((0.786m, 0.786m), cd / xc)) yield break;
        }
        else
        {
            var ad = Math.Abs(a.Price - d) / xa;
            if (!Within(s.AbXa, ab / xa) || !Within(s.BcAb, bc / ab) || !Within(s.CdBc, cd / bc) || !Within(s.AdXa, ad)) yield break;
            if (bull ? d >= b.Price : d <= b.Price) yield break;
        }

        var legForSl = s.AbXa is null ? ab : xa;
        var sl = bull ? d - c.P["slXaFrac"] * legForSl : d + c.P["slXaFrac"] * legForSl;
        var target = bull ? d + c.P["targetFib"] * Math.Abs(a.Price - d) : d - c.P["targetFib"] * Math.Abs(a.Price - d);
        var breakout = bull ? c.Bars[i].High : c.Bars[i].Low;
        var quality = c.P["quality"] * (1 - 0.4m * (deviations.Count == 0 ? 0 : deviations.Average()));
        Pivot[] pivots = [x, a, b, cc, new(i, c.Bars[i].Time, d, !bull)];
        var setup = SetupMath.Measured(code, dir, breakout, sl, target, quality, pivots, $"{code}@{string.Join(',', t.Select(p => p.Time.Ticks))}");
        if (setup is not null) yield return setup;
    }
}
