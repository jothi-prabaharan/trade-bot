using TradeBot.Application.Market;
using TradeBot.Application.Patterns;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

internal static class BarFactory
{
    public static readonly DateTime Session = new DateTime(2026, 10, 27, 9, 15, 0, DateTimeKind.Utc).AddMinutes(-330);

    /// <summary>Bars following straight legs from <paramref name="start"/>; each bar opens at the previous close.</summary>
    public static List<Bar> Path(decimal start, params (int Bars, decimal To)[] legs)
    {
        var bars = new List<Bar>();
        var price = start;
        foreach (var (n, to) in legs)
        {
            var step = (to - price) / n;
            for (var k = 0; k < n; k++)
            {
                var close = price + step;
                bars.Add(new Bar(Session.AddMinutes(5 * bars.Count), price, Math.Max(price, close) + 0.1m, Math.Min(price, close) - 0.1m, close, 1000));
                price = close;
            }
        }
        return bars;
    }

    public static List<Bar> Flat(decimal price, int n) => Path(price, (n, price));

    public static Bar Next(List<Bar> bars, decimal open, decimal high, decimal low, decimal close, long volume = 1000) =>
        new(bars[^1].Time.AddMinutes(5), open, high, low, close, volume);

    public static List<Bar> RandomWalk(int n, int seed)
    {
        var rng = new Random(seed);
        var bars = new List<Bar>();
        var price = 25000m;
        for (var i = 0; i < n; i++)
        {
            var open = price;
            var close = Math.Max(100, open + (decimal)(rng.NextDouble() - 0.5) * 60);
            var high = Math.Max(open, close) + (decimal)rng.NextDouble() * 20;
            var low = Math.Min(open, close) - (decimal)rng.NextDouble() * 20;
            bars.Add(new Bar(Session.AddMinutes(5 * i), open, high, low, close, 500 + rng.Next(1500)));
            price = close;
        }
        return bars;
    }
}

public sealed class PatternEngineTests
{
    private static readonly SeriesKey Series = new(10, null);

    private static List<ChartPattern> Catalog(params string[] codes) =>
        PatternRegistry.All.Select(d => PatternRegistry.ToCatalogRow(d.Definition))
            .Select(r => { r.IsActive = codes.Length == 0 || codes.Contains(r.PatternCode); return r; })
            .ToList();

    private static List<ConfirmedPattern> Feed(PatternEngine engine, List<Bar> bars, MarketBias trend = MarketBias.Neutral)
    {
        var all = new List<ConfirmedPattern>();
        for (var k = 2; k <= bars.Count; k++) all.AddRange(engine.OnBarClosed(Series, Timeframe.M5, bars.Take(k).ToList(), trend));
        return all;
    }

    [Fact]
    public void Registry_has_80_plus_unique_patterns_across_four_families()
    {
        var defs = PatternRegistry.All.Select(d => d.Definition).ToList();
        Assert.True(defs.Count >= 80, $"only {defs.Count}");
        Assert.Equal(defs.Count, defs.Select(d => d.Code).Distinct().Count());
        Assert.Equal(4, defs.Select(d => d.Category).Distinct().Count());
        Assert.True(defs.Count(d => d.Category == PatternCategory.Candlestick) >= 60);
        Assert.All(defs, d =>
        {
            Assert.NotNull(d.DefaultParameters["confirmWithinBars"]);
            Assert.InRange(d.DefaultWeight, 0, 100);
        });
        Assert.All(PatternRegistry.All.Select(d => PatternRegistry.ToCatalogRow(d.Definition)),
            r => Assert.Equal(r.Category is PatternCategory.Candlestick or PatternCategory.Classical, r.IsActive));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Every_detector_runs_on_random_data_and_confirmations_are_well_formed(int seed)
    {
        var bars = BarFactory.RandomWalk(260, seed);
        foreach (var detector in PatternRegistry.All)
        {
            var p = new PatternParams(detector.Definition.DefaultParameters);
            for (var k = 2; k <= bars.Count; k++)
                foreach (var s in detector.Detect(new PatternContext(bars.Take(k).ToList(), p)).ToList())
                {
                    Assert.Equal(detector.Definition.Code, s.Code);
                    Assert.Contains(s.Direction, new[] { PatternDirection.Bullish, PatternDirection.Bearish });
                }
        }

        var confirmed = Feed(new PatternEngine(Catalog()), bars, MarketBias.Bullish);
        Assert.NotEmpty(confirmed);
        Assert.All(confirmed, c =>
        {
            Assert.InRange(c.Confidence, 0, 100);
            if (c.Direction == PatternDirection.Bullish) Assert.True(c.StructuralSl < c.EntryReference && c.EntryReference < c.StructuralTarget, c.Code);
            else Assert.True(c.StructuralTarget < c.EntryReference && c.EntryReference < c.StructuralSl, c.Code);
        });
        Assert.Contains(confirmed, c => c.Category == PatternCategory.Candlestick);
    }

    [Fact]
    public void Bullish_engulfing_waits_for_breakout_close()
    {
        var bars = BarFactory.Path(110, (20, 100));
        bars.Add(BarFactory.Next(bars, 100, 100.2m, 98.8m, 99));
        bars.Add(BarFactory.Next(bars, 98.9m, 101.6m, 98.7m, 101.5m));
        var engine = new PatternEngine(Catalog("CDL_ENGULFING_BULL"));
        Assert.Empty(Feed(engine, bars));

        bars.Add(BarFactory.Next(bars, 101.5m, 102.4m, 101.4m, 102.2m));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
        Assert.Equal(("CDL_ENGULFING_BULL", PatternDirection.Bullish, 101.6m), (c.Code, c.Direction, c.BreakoutPrice));
        Assert.True(c.StructuralSl < 98.7m);
        Assert.Equal(c.BreakoutPrice + 2 * (c.BreakoutPrice - c.StructuralSl), c.StructuralTarget);
    }

    [Fact]
    public void Neutral_doji_confirms_on_the_side_that_breaks()
    {
        var bars = BarFactory.Path(100, (20, 102));
        bars.Add(BarFactory.Next(bars, 102, 103, 101, 102.01m));
        var engine = new PatternEngine(Catalog("CDL_DOJI"));
        Feed(engine, bars);
        bars.Add(BarFactory.Next(bars, 102, 102.2m, 100.5m, 100.6m));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Neutral));
        Assert.Equal(PatternDirection.Bearish, c.Direction);
    }

    [Fact]
    public void Breakout_after_expiry_window_is_ignored()
    {
        var bars = BarFactory.Path(110, (20, 100));
        bars.Add(BarFactory.Next(bars, 100, 100.2m, 98.8m, 99));
        bars.Add(BarFactory.Next(bars, 98.9m, 101.6m, 98.7m, 101.5m));
        for (var k = 0; k < 4; k++) bars.Add(BarFactory.Next(bars, 101.2m, 101.5m, 101m, 101.2m));
        bars.Add(BarFactory.Next(bars, 101.2m, 103m, 101m, 102.8m));
        Assert.Empty(Feed(new PatternEngine(Catalog("CDL_ENGULFING_BULL")), bars));
    }

    [Fact]
    public void Double_bottom_confirms_on_neckline_breakout_with_volume()
    {
        var bars = BarFactory.Flat(120, 25);
        bars.AddRange(BarFactory.Path(120, (15, 100), (8, 110), (8, 100.2m), (6, 108)).Select((b, k) => b with { Time = bars[^1].Time.AddMinutes(5 * (k + 1)) }));
        var engine = new PatternEngine(Catalog("CLS_DOUBLE_BOTTOM"));
        Assert.Empty(Feed(engine, bars));

        bars.Add(BarFactory.Next(bars, 108, 111.3m, 107.9m, 111.2m, volume: 3000));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
        Assert.Equal(PatternDirection.Bullish, c.Direction);
        Assert.InRange(c.BreakoutPrice, 109.8m, 110.2m);
        Assert.InRange(c.StructuralSl, 99m, 100m);
        Assert.InRange(c.StructuralTarget, 119.5m, 120.5m);
        Assert.True(c.VolumeRatio >= 2.5m);
    }

    [Fact]
    public void Double_bottom_breakout_without_volume_stays_pending()
    {
        var bars = BarFactory.Flat(120, 25);
        bars.AddRange(BarFactory.Path(120, (15, 100), (8, 110), (8, 100.2m), (6, 108)).Select((b, k) => b with { Time = bars[^1].Time.AddMinutes(5 * (k + 1)) }));
        var engine = new PatternEngine(Catalog("CLS_DOUBLE_BOTTOM"));
        Feed(engine, bars);
        bars.Add(BarFactory.Next(bars, 108, 111.3m, 107.9m, 111.2m, volume: 1000));
        Assert.Empty(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
        bars.Add(BarFactory.Next(bars, 111.2m, 112.5m, 111m, 112.3m, volume: 2500));
        Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
    }

    [Fact]
    public void Head_and_shoulders_breaks_down_through_neckline()
    {
        var bars = BarFactory.Flat(100, 25);
        bars.AddRange(BarFactory.Path(100, (8, 110), (5, 104), (10, 116), (10, 104.5m), (5, 110.3m), (3, 107)).Select((b, k) => b with { Time = bars[^1].Time.AddMinutes(5 * (k + 1)) }));
        var engine = new PatternEngine(Catalog("CLS_HEAD_SHOULDERS"));
        Assert.Empty(Feed(engine, bars));

        bars.Add(BarFactory.Next(bars, 107, 107.1m, 102.8m, 103, volume: 3000));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bearish));
        Assert.Equal(PatternDirection.Bearish, c.Direction);
        Assert.True(c.StructuralSl > 110.3m && c.StructuralTarget < 95m);
        Assert.True(c.Confidence >= 70, $"confidence {c.Confidence}");
    }

    [Fact]
    public void Bull_flag_breakout_targets_pole_height()
    {
        var bars = BarFactory.Flat(100, 30);
        bars.AddRange(BarFactory.Path(100, (5, 110), (6, 107)).Select((b, k) => b with { Time = bars[^1].Time.AddMinutes(5 * (k + 1)) }));
        var engine = new PatternEngine(Catalog("CLS_BULL_FLAG"));
        Assert.Empty(Feed(engine, bars));

        bars.Add(BarFactory.Next(bars, 107, 110.6m, 106.9m, 110.5m, volume: 3000));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
        Assert.Equal("CLS_BULL_FLAG", c.Code);
        Assert.True(c.StructuralTarget - c.BreakoutPrice >= 9.5m);
    }

    [Fact]
    public void Bullish_gartley_completes_at_d_and_confirms_on_reversal()
    {
        // Pivots (incl. 0.1 wicks): X=100, A=120, B=107.64 (0.618 XA), C=113.82 (0.5 AB), D=104.28 (0.786 XA)
        var bars = BarFactory.Flat(103, 30);
        bars.AddRange(BarFactory.Path(103, (5, 100.1m), (16, 119.9m), (10, 107.74m), (6, 113.72m), (8, 104.38m)).Select((b, k) => b with { Time = bars[^1].Time.AddMinutes(5 * (k + 1)) }));
        var engine = new PatternEngine(Catalog("HRM_GARTLEY_BULL"));
        Feed(engine, bars);
        bars.Add(BarFactory.Next(bars, 104.3m, 106.6m, 104.2m, 106.5m));
        bars.Add(BarFactory.Next(bars, 106.5m, 107.5m, 106.3m, 107.4m));
        var all = engine.OnBarClosed(Series, Timeframe.M5, bars.Take(bars.Count - 1).ToList(), MarketBias.Neutral)
            .Concat(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Neutral)).ToList();
        var c = Assert.Single(all);
        Assert.Equal(("HRM_GARTLEY_BULL", PatternDirection.Bullish), (c.Code, c.Direction));
        Assert.True(c.StructuralSl < 104.28m && c.StructuralTarget > 110m);
    }

    [Fact]
    public void Bos_bull_triggers_on_close_above_last_swing_high_in_uptrend()
    {
        var bars = BarFactory.Flat(100, 20);
        bars.AddRange(BarFactory.Path(100, (4, 104), (3, 102), (4, 107), (3, 104.5m), (3, 106.5m)).Select((b, k) => b with { Time = bars[^1].Time.AddMinutes(5 * (k + 1)) }));
        var engine = new PatternEngine(Catalog("SMC_BOS_BULL"));
        Assert.Empty(Feed(engine, bars));
        bars.Add(BarFactory.Next(bars, 106.5m, 108.2m, 106.4m, 108, volume: 3000));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
        Assert.Equal(108, c.EntryReference);
        Assert.True(c.StructuralSl < 104.5m);
    }

    [Fact]
    public void Cpr_breakout_uses_previous_session_pivots()
    {
        var day1 = BarFactory.Path(100, (30, 110), (30, 104), (15, 105));            // H≈110.1 L≈99.9 C=105
        var day2Start = BarFactory.Session.AddDays(1);
        var day2 = BarFactory.Path(105, (25, 103.5m)).Select((b, k) => b with { Time = day2Start.AddMinutes(5 * k) }).ToList();
        var bars = day1.Concat(day2).ToList();
        var engine = new PatternEngine(Catalog("SMC_CPR_BREAKOUT_BULL"));
        Assert.Empty(Feed(engine, bars));
        bars.Add(BarFactory.Next(bars, 103.5m, 106.2m, 103.4m, 106, volume: 3000));
        var c = Assert.Single(engine.OnBarClosed(Series, Timeframe.M5, bars, MarketBias.Bullish));
        Assert.True(c.StructuralTarget > 106 && c.StructuralSl < 105);
    }

    [Fact]
    public void Disabled_patterns_never_fire()
    {
        var bars = BarFactory.RandomWalk(200, 11);
        var rows = Catalog();
        rows.ForEach(r => r.IsActive = false);
        var engine = new PatternEngine(rows);
        Assert.Equal(0, engine.ActiveCount);
        Assert.Empty(Feed(engine, bars));
    }

    [Theory]
    [InlineData(70, 0.8, PatternDirection.Bullish, MarketBias.Bullish, 1.0, 74.4)]
    [InlineData(70, 0.8, PatternDirection.Bullish, MarketBias.Bearish, 1.0, 49.4)]
    [InlineData(70, 1.0, PatternDirection.Bearish, MarketBias.Bearish, 3.0, 90.0)]
    [InlineData(40, 0.8, PatternDirection.Bullish, MarketBias.Neutral, 0.2, 31.8)]
    public void Confidence_scoring(double weight, double quality, PatternDirection dir, MarketBias trend, double volRatio, double expected) =>
        Assert.Equal((decimal)expected, PatternEngine.Score((decimal)weight, (decimal)quality, dir, trend, (decimal)volRatio, hasVolume: true));
}
