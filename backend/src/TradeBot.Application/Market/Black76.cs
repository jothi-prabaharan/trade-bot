namespace TradeBot.Application.Market;

public sealed record Greeks(double Price, double Iv, double Delta, double Gamma, double ThetaPerDay, double VegaPerPct);

/// <summary>Black-76 for options on futures; <c>F</c> is the futures price, <c>T</c> in years.</summary>
public static class Black76
{
    public const double DefaultRate = 0.065;
    private const double MinVol = 0.005, MaxVol = 5.0;

    public static double Price(double f, double k, double t, double vol, bool isCall, double r = DefaultRate)
    {
        if (t <= 0 || vol <= 0) return Math.Max(isCall ? f - k : k - f, 0);
        var (d1, d2) = D(f, k, t, vol);
        var df = Math.Exp(-r * t);
        return isCall ? df * (f * N(d1) - k * N(d2)) : df * (k * N(-d2) - f * N(-d1));
    }

    public static Greeks Compute(double f, double k, double t, double vol, bool isCall, double r = DefaultRate)
    {
        var price = Price(f, k, t, vol, isCall, r);
        if (t <= 0 || vol <= 0) return new Greeks(price, vol, isCall ? (f > k ? 1 : 0) : (f < k ? -1 : 0), 0, 0, 0);
        var (d1, d2) = D(f, k, t, vol);
        var df = Math.Exp(-r * t);
        var sqrtT = Math.Sqrt(t);
        var delta = isCall ? df * N(d1) : -df * N(-d1);
        var gamma = df * Pdf(d1) / (f * vol * sqrtT);
        var vega = f * df * Pdf(d1) * sqrtT;
        var theta = -f * df * Pdf(d1) * vol / (2 * sqrtT) + r * price; // same form for calls and puts
        return new Greeks(price, vol, delta, gamma, theta / 365.0, vega / 100.0);
    }

    /// <summary>Implied volatility by Newton with bisection fallback; null when the price is below intrinsic.</summary>
    public static double? ImpliedVol(double marketPrice, double f, double k, double t, bool isCall, double r = DefaultRate)
    {
        if (t <= 0 || marketPrice <= 0) return null;
        var intrinsic = Math.Exp(-r * t) * Math.Max(isCall ? f - k : k - f, 0);
        if (marketPrice < intrinsic - 1e-6) return null;

        double lo = MinVol, hi = MaxVol;
        if (Price(f, k, t, hi, isCall, r) < marketPrice) return null;
        var vol = 0.2;
        for (var i = 0; i < 100; i++)
        {
            var p = Price(f, k, t, vol, isCall, r);
            var diff = p - marketPrice;
            if (Math.Abs(diff) < 1e-6) return vol;
            if (diff > 0) hi = vol; else lo = vol;
            var vega = f * Math.Exp(-r * t) * Pdf(D(f, k, t, vol).D1) * Math.Sqrt(t);
            var next = vega > 1e-8 ? vol - diff / vega : double.NaN;
            vol = double.IsNaN(next) || next <= lo || next >= hi ? (lo + hi) / 2 : next;
        }
        return vol;
    }

    /// <summary>Years to expiry, using the 15:30 IST close on expiry day.</summary>
    public static double YearsToExpiry(DateOnly expiry, DateTime nowUtc)
    {
        var expiryUtc = expiry.ToDateTime(new TimeOnly(15, 30)) - Validation.FixedLimits.IstOffset;
        return Math.Max((expiryUtc - nowUtc).TotalDays, 0) / 365.0;
    }

    private static (double D1, double D2) D(double f, double k, double t, double vol)
    {
        var d1 = (Math.Log(f / k) + 0.5 * vol * vol * t) / (vol * Math.Sqrt(t));
        return (d1, d1 - vol * Math.Sqrt(t));
    }

    private static double Pdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    // Abramowitz-Stegun 7.1.26 via erf; max error ~1.5e-7
    private static double N(double x)
    {
        var z = Math.Abs(x) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * z);
        var erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }
}
