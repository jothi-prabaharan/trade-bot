using System.Text.Json.Nodes;
using TradeBot.Application.Patterns;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Trading;

public sealed record SignalInputs(
    ConfirmedPattern Pattern, long DetectionId, Instrument Instrument, SecurityMaster Option,
    decimal FuturesPrice, decimal SpotPrice, decimal OptionLtp, DateTime NowUtc,
    decimal? NetSentiment, long? CatalystNewsId, OiSnapshot? Oi, GeminiNewsConfig Weights, BracketPolicy ReferencePolicy);

public sealed record BuiltSignal(Signal Signal, StructuralPremiums? Structural, PremiumBracket Bracket);

/// <summary>Turns a confirmed underlying pattern into an option BUY signal with its mandatory bracket and scores.</summary>
public static class SignalBuilder
{
    public static OptionType OptionFor(PatternDirection dir) => dir == PatternDirection.Bullish ? OptionType.Ce : OptionType.Pe;

    public static BuiltSignal Build(SignalInputs x)
    {
        var o = x.Option;
        var structural = OptionBracket.MapStructural(
            x.OptionLtp, x.FuturesPrice, o.StrikePrice!.Value, o.OptType!.Value, o.ExpiryDate!.Value, x.NowUtc,
            x.Pattern.StructuralSl, x.Pattern.StructuralTarget);
        var bracket = OptionBracket.Compute(x.OptionLtp, structural, x.ReferencePolicy, o.TickSize);

        var newsScore = SignalScoring.NewsScore(x.Pattern.Direction, x.NetSentiment);
        var oiScore = SignalScoring.OiGreeksScore(x.Pattern.Direction, x.Oi, structural?.Greeks.Delta);
        var g = structural?.Greeks;

        var details = new JsonObject
        {
            ["patternCode"] = x.Pattern.Code,
            ["futures"] = x.FuturesPrice,
            ["strike"] = o.StrikePrice,
            ["expiry"] = o.ExpiryDate!.Value.ToString("yyyy-MM-dd"),
            ["iv"] = g?.Iv, ["delta"] = g?.Delta, ["gamma"] = g?.Gamma, ["thetaPerDay"] = g?.ThetaPerDay, ["vegaPerPct"] = g?.VegaPerPct,
            ["underlyingSl"] = x.Pattern.StructuralSl, ["underlyingTarget"] = x.Pattern.StructuralTarget,
            ["structuralPremiumSl"] = structural?.StopLoss, ["structuralPremiumTarget"] = structural?.Target,
            ["entryReference"] = bracket.Entry,
            ["pcr"] = x.Oi?.PutCallRatio, ["callOiChange"] = x.Oi?.CallOiChange, ["putOiChange"] = x.Oi?.PutOiChange,
        };

        var signal = new Signal
        {
            TradingDate = DateOnly.FromDateTime(FixedLimits.ToIst(x.NowUtc)),
            DetectionId = x.DetectionId,
            NewsId = x.CatalystNewsId,
            InstrumentId = x.Instrument.InstrumentId,
            SecurityId = o.SecurityId,
            SignalType = SignalType.Buy,
            SpotPrice = x.SpotPrice,
            OptionDelta = g is null ? null : Math.Round((decimal)g.Delta, 4),
            EntryOptionLtp = x.OptionLtp,
            MandatorySlPrice = bracket.StopLoss,
            MandatoryTargetPrice = bracket.Target,
            SlMethod = bracket.Method,
            PatternScore = x.Pattern.Confidence,
            NewsScore = newsScore,
            OiGreeksScore = oiScore,
            CombinedAiConfidence = SignalScoring.Combined(x.Weights, x.Pattern.Confidence, newsScore, oiScore),
            GreeksAndOi = details.ToJsonString(),
        };
        return new BuiltSignal(signal, structural, bracket);
    }

    /// <summary>Re-derives the bracket for an account whose risk policy differs from the signal's reference policy.</summary>
    public static PremiumBracket ForAccount(Signal signal, decimal ltpNow, BracketPolicy policy, decimal tick)
    {
        var d = JsonNode.Parse(signal.GreeksAndOi) as JsonObject;
        var sl = d?["structuralPremiumSl"]?.GetValue<decimal>();
        var target = d?["structuralPremiumTarget"]?.GetValue<decimal>();
        var structural = sl is { } s && target is { } t ? new StructuralPremiums(s, t, new Market.Greeks(0, 0, 0, 0, 0, 0)) : null;
        return OptionBracket.Compute(ltpNow, structural, policy, tick);
    }
}
