using System.Text.RegularExpressions;

namespace TradeBot.Application.Security;

/// <summary>
/// Strips credentials from text before it reaches a log, an event or an alert: JSON values under token/secret/key names,
/// the same names in query strings (Playwright errors quote the login URL), JWTs anywhere, and any known secret values.
/// </summary>
public static partial class SecretRedactor
{
    public const string Hidden = "[redacted]";

    public static string Redact(string? text, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // Longest first, so a secret that contains another is hidden whole. Short values (a PIN) never go into URLs or
        // bodies, and replacing them would mangle ordinary numbers in messages.
        foreach (var secret in knownSecrets.Where(s => s is { Length: >= 8 }).OrderByDescending(s => s!.Length))
            text = text.Replace(secret!, Hidden, StringComparison.Ordinal);
        text = JsonSecret().Replace(text, m => m.Groups[1].Value + Hidden + m.Groups[3].Value);
        text = QuerySecret().Replace(text, m => m.Groups[1].Value + Hidden);
        return Jwt().Replace(text, Hidden);
    }

    /// <summary>"••••ab12" for long identifiers (enough to tell keys apart), "••••" for short ones; null stays null.</summary>
    public static string? Mask(string? value) =>
        value is null ? null : value.Length >= 12 ? "••••" + value[^4..] : "••••";

    [GeneratedRegex("""("(?:[A-Za-z_-]*token|[A-Za-z_-]*secret[A-Za-z_-]*|api[_-]?key|password|passcode|pin|mpin|otp|totp|authorization)"\s*:\s*")([^"]*)(")""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    [GeneratedRegex("""((?:^|[?&;\s])(?:api[_-]?key|api[_-]?secret[_-]?key|api[_-]?secret|request[_-]?token|access[_-]?token|token|secret|password)=)[^&#\s"']+""", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();
}
