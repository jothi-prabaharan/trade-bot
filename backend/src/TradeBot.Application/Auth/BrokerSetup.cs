using TradeBot.Application.Abstractions;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Application.Auth;

public sealed record SetupItem(string Key, string Label, bool Ok, bool Required, string Detail);

public sealed record BrokerSetupReport(
    long AccountId, IReadOnlyList<SetupItem> Items, DateTime ServerTimeUtc, string ServerTimeIst, double? ClockSkewSeconds, string? ClockWarning,
    int TotpSecondsRemaining, BrokerConnectionCheck? LastConnection);

/// <summary>MATCH, CLOCK_OFF, NO_MATCH, BAD_CODE, NO_SECRET or BAD_SECRET, with what it means.</summary>
public sealed record TotpComparison(string Result, string Message, int? StepOffset, int SecondsRemaining);

/// <summary>
/// "Is this account ready for the headless login?" without sending anything to Paytm: the API key and secret as they
/// will be sent, the login fields, the TOTP secret, and the TOTP compared with the code in the user's authenticator app.
/// </summary>
public static class BrokerSetup
{
    public static IReadOnlyList<SetupItem> Items(TradingAccount a)
    {
        var loginId = BrokerCredentials.Clean(a.LoginId ?? a.Phone ?? a.Email);
        var items = new List<SetupItem>
        {
            Credential("apiKey", "API key", "api_key", a.ApiKey, SecretRedactor.Mask(BrokerCredentials.Clean(a.ApiKey))!),
            Credential("apiSecret", "API secret", "api_secret_key", a.ApiSecret, "Set"),
            new("loginId", "Login ID", loginId is not null, true, loginId is null ? "Missing: the mobile number or email you log in to Paytm Money with" : MaskLogin(loginId)),
            new("loginPassword", "Login password", !string.IsNullOrEmpty(a.LoginPassword), true, string.IsNullOrEmpty(a.LoginPassword) ? "Missing" : "Set"),
            TotpSecretItem(a.TotpSecret),
            new("loginPin", "Login PIN", true, false, string.IsNullOrEmpty(a.LoginPin) ? "Not set (only needed if Paytm asks for a PIN)" : "Set"),
        };
        return items;
    }

    public static TotpComparison CompareTotp(TradingAccount a, string? code, DateTime utcNow)
    {
        var left = Totp.SecondsRemaining(utcNow);
        if (string.IsNullOrWhiteSpace(a.TotpSecret)) return new("NO_SECRET", "No TOTP secret is saved for this account.", null, left);
        if (TotpProblem(a.TotpSecret) is { } problem) return new("BAD_SECRET", problem, null, left);
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 6) return new("BAD_CODE", "Enter the 6-digit code your authenticator app shows for Paytm Money.", null, left);

        var offset = Totp.MatchStep(a.TotpSecret, digits, utcNow);
        var intoStep = 30 - left;
        return offset switch
        {
            0 => new("MATCH", "Matches: the saved TOTP secret is the one in your authenticator app, and the server clock agrees with it.", 0, left),
            // The code changed while it was being typed
            -1 when intoStep <= 10 => new("MATCH", "Matches the code that changed while you typed it: the secret and the server clock are right.", -1, left),
            { } o => new("CLOCK_OFF",
                $"The secret is right, but the server clock is about {Math.Abs(o) * 30} s {(o < 0 ? "ahead of" : "behind")} your phone, so Paytm can reject " +
                "the bot's codes. Turn on time sync on the server (sudo timedatectl set-ntp true).", o, left),
            null => new("NO_MATCH",
                "Does not match: the saved TOTP secret is not the one your authenticator app uses for Paytm Money (or the clocks are minutes apart). " +
                "Copy the secret from Paytm Money's TOTP setup again and save it in the profile.", null, left),
        };
    }

    /// <summary>Checked as it will be sent to POST /accounts/v2/gettoken (<paramref name="wire"/> is the body field).</summary>
    private static SetupItem Credential(string key, string field, string wire, string? value, string okDetail) =>
        BrokerCredentials.Problem(field, value) is { } p
            ? new(key, $"{field} ({wire})", false, true, BrokerCredentials.Clean(value) is null ? "Missing" : p)
            : new(key, $"{field} ({wire})", true, true, okDetail);

    private static SetupItem TotpSecretItem(string? secret) => TotpProblem(secret) is { } p
        ? new("totpSecret", "TOTP secret", false, true, p)
        : new("totpSecret", "TOTP secret", true, true, $"Valid Base32 ({Totp.Base32Decode(secret!).Length * 8}-bit), generates 6-digit codes every 30 s");

    public static string? TotpProblem(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "Missing: enable TOTP in Paytm Money and save the secret it shows";
        try
        {
            return Totp.Base32Decode(secret).Length < 10 ? "Too short: a TOTP secret has at least 16 Base32 characters" : null;
        }
        catch (FormatException) { return "Not Base32: only letters A–Z and digits 2–7"; }
    }

    /// <summary>"98******10" / "as***@example.in": enough to recognise, not to copy.</summary>
    public static string MaskLogin(string id)
    {
        var at = id.IndexOf('@');
        if (at > 0) return id[..Math.Min(2, at)] + "***" + id[at..];
        return id.Length <= 4 ? "****" : id[..2] + new string('*', id.Length - 4) + id[^2..];
    }
}
