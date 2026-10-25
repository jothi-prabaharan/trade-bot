using TradeBot.Domain.Enums;

namespace TradeBot.Application.Market;

public static class Indicators
{
    public static decimal[] Ema(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal[values.Count];
        if (values.Count == 0) return result;
        var k = 2m / (period + 1);
        result[0] = values[0];
        for (var i = 1; i < values.Count; i++) result[i] = values[i] * k + result[i - 1] * (1 - k);
        return result;
    }

    /// <summary>Wilder ATR; entries before <paramref name="period"/> bars use the running mean of true range.</summary>
    public static decimal[] Atr(IReadOnlyList<Bar> bars, int period = 14)
    {
        var atr = new decimal[bars.Count];
        decimal sum = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            var tr = i == 0 ? bars[i].Range
                : Math.Max(bars[i].Range, Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close)));
            if (i < period)
            {
                sum += tr;
                atr[i] = sum / (i + 1);
            }
            else atr[i] = (atr[i - 1] * (period - 1) + tr) / period;
        }
        return atr;
    }

    public static decimal AverageVolume(IReadOnlyList<Bar> bars, int endExclusive, int lookback = 20)
    {
        var start = Math.Max(0, endExclusive - lookback);
        if (endExclusive <= start) return 0;
        decimal sum = 0;
        for (var i = start; i < endExclusive; i++) sum += bars[i].Volume;
        return sum / (endExclusive - start);
    }

    /// <summary>Session VWAP over bars from the latest session start (bars must be intraday, ascending).</summary>
    public static decimal? SessionVwap(IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0) return null;
        var sessionDate = Validation.FixedLimits.ToIst(bars[^1].Time).Date;
        decimal pv = 0, vol = 0;
        for (var i = bars.Count - 1; i >= 0 && Validation.FixedLimits.ToIst(bars[i].Time).Date == sessionDate; i--)
        {
            var typical = (bars[i].High + bars[i].Low + bars[i].Close) / 3;
            pv += typical * bars[i].Volume;
            vol += bars[i].Volume;
        }
        return vol == 0 ? null : pv / vol;
    }

    /// <summary>15m trend bias: EMA20 vs EMA50 with price on the right side of EMA20.</summary>
    public static MarketBias TrendBias(IReadOnlyList<Bar> bars, int fast = 20, int slow = 50)
    {
        if (bars.Count < slow) return MarketBias.Neutral;
        var closes = bars.Select(b => b.Close).ToArray();
        var f = Ema(closes, fast)[^1];
        var s = Ema(closes, slow)[^1];
        var last = closes[^1];
        if (f > s && last > f) return MarketBias.Bullish;
        if (f < s && last < f) return MarketBias.Bearish;
        return MarketBias.Neutral;
    }

    /// <summary>True when the latest bar's volume exceeds multiplier × average of the previous bars.</summary>
    public static bool IsVolumeSpike(IReadOnlyList<Bar> bars, decimal multiplier, int lookback = 20)
    {
        if (bars.Count < lookback + 1) return false;
        var avg = AverageVolume(bars, bars.Count - 1, lookback);
        return avg > 0 && bars[^1].Volume > avg * multiplier;
    }
}
