using TradeBot.Application.Market;

namespace TradeBot.Application.Patterns;

/// <summary>TA-Lib style candle measurements; averages use the bars before the candle being judged.</summary>
public sealed class CandleKit(IReadOnlyList<Bar> b)
{
    private const int BodyPeriod = 10, NearPeriod = 5;

    public decimal O(int i) => b[i].Open;
    public decimal C(int i) => b[i].Close;
    public decimal H(int i) => b[i].High;
    public decimal L(int i) => b[i].Low;
    public decimal Body(int i) => Math.Abs(C(i) - O(i));
    public decimal Range(int i) => H(i) - L(i);
    public decimal Up(int i) => H(i) - Top(i);
    public decimal Lo(int i) => Bot(i) - L(i);
    public decimal Top(int i) => Math.Max(O(i), C(i));
    public decimal Bot(int i) => Math.Min(O(i), C(i));
    public bool Bull(int i) => C(i) > O(i);
    public bool Bear(int i) => C(i) < O(i);

    public bool LongBody(int i) => Body(i) > Avg(i, BodyPeriod, Body);
    public bool VeryLongBody(int i) => Body(i) > 3 * Avg(i, BodyPeriod, Body);
    public bool SmallBody(int i) => Body(i) < Avg(i, BodyPeriod, Body);
    public bool Doji(int i) => Body(i) <= 0.1m * Avg(i, BodyPeriod, Range);
    public bool UpVeryShort(int i) => Up(i) < 0.1m * Avg(i, BodyPeriod, Range);
    public bool LoVeryShort(int i) => Lo(i) < 0.1m * Avg(i, BodyPeriod, Range);
    public bool UpShort(int i) => Up(i) < Avg(i, BodyPeriod, j => (Up(j) + Lo(j)) / 2);
    public bool LoShort(int i) => Lo(i) < Avg(i, BodyPeriod, j => (Up(j) + Lo(j)) / 2);
    public decimal Near(int i) => 0.2m * Avg(i, NearPeriod, Range);
    public decimal Far(int i) => 0.6m * Avg(i, NearPeriod, Range);
    public decimal Equal(int i) => 0.05m * Avg(i, NearPeriod, Range);

    public bool Marubozu(int i) => LongBody(i) && UpVeryShort(i) && LoVeryShort(i);
    public bool BodyGapUp(int i) => Bot(i) > Top(i - 1);
    public bool BodyGapDown(int i) => Top(i) < Bot(i - 1);
    public bool Inside(int i) => H(i) < H(i - 1) && L(i) > L(i - 1);

    /// <summary>Close of the bar before <paramref name="i"/> is below the close five bars earlier.</summary>
    public bool DownTrend(int i, int lookback = 5) => i - 1 - lookback >= 0 && C(i - 1) < C(i - 1 - lookback);
    public bool UpTrend(int i, int lookback = 5) => i - 1 - lookback >= 0 && C(i - 1) > C(i - 1 - lookback);

    private decimal Avg(int i, int period, Func<int, decimal> f)
    {
        var start = Math.Max(0, i - period);
        if (start >= i) return f(i);
        decimal sum = 0;
        for (var j = start; j < i; j++) sum += f(j);
        return sum / (i - start);
    }
}
