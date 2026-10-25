using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Trading;

/// <summary>Options-flow snapshot for the selected expiry (from the option-chain API).</summary>
public sealed record OiSnapshot(decimal PutCallRatio, long CallOiChange, long PutOiChange);

/// <summary>Combines pattern, news and OI/Greeks scores with mst.gemini_news_config weights.</summary>
public static class SignalScoring
{
    /// <summary>50 is neutral; aligned net sentiment (−100..100) moves it toward 100.</summary>
    public static decimal NewsScore(PatternDirection dir, decimal? netSentiment)
    {
        if (netSentiment is not { } net) return 50;
        var aligned = dir == PatternDirection.Bullish ? net : -net;
        return Math.Clamp(50 + aligned / 2, 0, 100);
    }

    public static decimal OiGreeksScore(PatternDirection dir, OiSnapshot? oi, double? delta)
    {
        decimal score = 50;
        var bull = dir == PatternDirection.Bullish;
        if (oi is not null)
        {
            score += Math.Clamp((bull ? oi.PutCallRatio - 1 : 1 - oi.PutCallRatio) * 40, -25, 25);
            // Put writing supports upside, call writing caps it
            if (oi.PutOiChange != oi.CallOiChange)
                score += (oi.PutOiChange > oi.CallOiChange) == bull ? 10 : -10;
        }
        if (delta is { } d) score += Math.Abs(d) is >= 0.4 and <= 0.6 ? 5 : -5;
        return Math.Clamp(Math.Round(score, 2), 0, 100);
    }

    public static decimal Combined(GeminiNewsConfig w, decimal pattern, decimal news, decimal oiGreeks) =>
        Math.Round((w.PatternWeightPct * pattern + w.NewsWeightPct * news + w.OiGreeksWeightPct * oiGreeks) / 100, 2);
}
