using TradeBot.Application.Market;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

public sealed class CandleAggregatorTests
{
    private static readonly SeriesKey Nifty = new(10, null);
    private static DateTime Ist(int h, int m, int s = 0) => new DateTime(2026, 10, 27, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    [Theory]
    [InlineData(9, 15, 0, Timeframe.M15, 9, 15)]
    [InlineData(9, 29, 59, Timeframe.M15, 9, 15)]
    [InlineData(9, 30, 0, Timeframe.M15, 9, 30)]
    [InlineData(10, 14, 0, Timeframe.H1, 9, 15)]
    [InlineData(10, 15, 0, Timeframe.H1, 10, 15)]
    [InlineData(9, 17, 30, Timeframe.M3, 9, 15)]
    public void Buckets_anchor_to_0915(int h, int m, int s, Timeframe tf, int eh, int em) =>
        Assert.Equal(Ist(eh, em), Timeframes.BucketStart(Ist(h, m, s), tf));

    [Fact]
    public void Ticks_outside_session_are_ignored()
    {
        Assert.Null(Timeframes.BucketStart(Ist(9, 14, 59), Timeframe.M1));
        Assert.Null(Timeframes.BucketStart(Ist(15, 30), Timeframe.M1));
    }

    [Fact]
    public void Builds_ohlcv_with_volume_deltas_and_closes_on_next_bucket()
    {
        var agg = new CandleAggregator([Timeframe.M1, Timeframe.M5]);
        Assert.Empty(agg.OnTick(Nifty, new Tick(1, 100, Ist(9, 15, 1), 1000)));
        agg.OnTick(Nifty, new Tick(1, 103, Ist(9, 15, 20), 1300));
        agg.OnTick(Nifty, new Tick(1, 99, Ist(9, 15, 40), 1500));
        var closed = agg.OnTick(Nifty, new Tick(1, 101, Ist(9, 16, 0), 1600)).ToList();

        var m1 = Assert.Single(closed);
        Assert.Equal(Timeframe.M1, m1.Timeframe);
        Assert.Equal(new Bar(Ist(9, 15), 100, 103, 99, 99, 500), m1.Bar);
        Assert.Equal(new Bar(Ist(9, 15), 100, 103, 99, 101, 600), agg.Forming(Nifty, Timeframe.M5));
    }

    [Fact]
    public void Flush_closes_idle_buckets()
    {
        var agg = new CandleAggregator([Timeframe.M1]);
        agg.OnTick(Nifty, new Tick(1, 100, Ist(9, 15, 5)));
        Assert.Empty(agg.Flush(Ist(9, 15, 59)));
        Assert.Single(agg.Flush(Ist(9, 16, 0)));
        Assert.Null(agg.Forming(Nifty, Timeframe.M1));
    }
}

public sealed class IndicatorTests
{
    private static List<Bar> Trend(int n, decimal step) =>
        Enumerable.Range(0, n).Select(i => { var c = 100 + i * step; return new Bar(DateTime.UtcNow.AddMinutes(i), c - step / 2, c + 1, c - 1, c, 1000); }).ToList();

    [Fact]
    public void Trend_bias_follows_ema_stack()
    {
        Assert.Equal(MarketBias.Bullish, Indicators.TrendBias(Trend(60, 1)));
        Assert.Equal(MarketBias.Bearish, Indicators.TrendBias(Trend(60, -1)));
        Assert.Equal(MarketBias.Neutral, Indicators.TrendBias(Trend(30, 1)));
    }

    [Fact]
    public void Atr_of_constant_range_bars() => Assert.Equal(2m, Math.Round(Indicators.Atr(Trend(30, 0))[^1], 4));

    [Fact]
    public void Volume_spike()
    {
        var bars = Trend(21, 1);
        bars[^1] = bars[^1] with { Volume = 2600 };
        Assert.True(Indicators.IsVolumeSpike(bars, 2.5m));
        Assert.False(Indicators.IsVolumeSpike(bars, 3m));
    }
}

public sealed class Black76Tests
{
    [Fact]
    public void Matches_reference_price() => Assert.Equal(7.9656, Black76.Price(100, 100, 1, 0.2, true, 0), 4);

    [Fact]
    public void Put_call_parity()
    {
        var c = Black76.Price(25100, 25000, 7 / 365.0, 0.13, true);
        var p = Black76.Price(25100, 25000, 7 / 365.0, 0.13, false);
        Assert.Equal((25100 - 25000) * Math.Exp(-Black76.DefaultRate * 7 / 365.0), c - p, 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Implied_vol_round_trips(bool isCall)
    {
        var price = Black76.Price(25080, 25000, 5 / 365.0, 0.145, isCall);
        Assert.Equal(0.145, Black76.ImpliedVol(price, 25080, 25000, 5 / 365.0, isCall)!.Value, 5);
    }

    [Fact]
    public void Atm_delta_near_half_and_put_negative()
    {
        var call = Black76.Compute(25000, 25000, 7 / 365.0, 0.13, true);
        var put = Black76.Compute(25000, 25000, 7 / 365.0, 0.13, false);
        Assert.InRange(call.Delta, 0.49, 0.52);
        Assert.InRange(put.Delta, -0.51, -0.48);
        Assert.True(call.ThetaPerDay < 0 && call.Gamma > 0 && call.VegaPerPct > 0);
    }

    [Fact]
    public void Below_intrinsic_has_no_iv() => Assert.Null(Black76.ImpliedVol(50, 25200, 25000, 5 / 365.0, true));
}

public sealed class ChargesTests
{
    [Fact]
    public void Round_trip_on_one_nifty_lot()
    {
        var buy = ChargesCalculator.ForOrder(ChargesCalculator.DefaultOptionRates, TxnType.Buy, 100.50m, 65);
        var sell = ChargesCalculator.ForOrder(ChargesCalculator.DefaultOptionRates, TxnType.Sell, 135m, 65);
        Assert.Equal(new ChargeBreakdown(20, 0, 2.29m, 0.01m, 0.20m, 4.01m), buy);
        Assert.Equal(new ChargeBreakdown(20, 8.78m, 3.07m, 0.01m, 0, 4.15m), sell);
        Assert.Equal(62.52m, (buy + sell).Total);
    }
}
