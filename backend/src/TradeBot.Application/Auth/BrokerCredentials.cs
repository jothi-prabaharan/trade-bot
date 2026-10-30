using System.Text.RegularExpressions;
using TradeBot.Application.Abstractions;

namespace TradeBot.Application.Auth;

/// <summary>Credentials as Paytm must receive them: nothing pasted along with them (spaces, line breaks, invisible characters).</summary>
public static partial class BrokerCredentials
{
    /// <summary>Trimmed, without zero-width characters; null when nothing is left.</summary>
    public static string? Clean(string? value)
    {
        if (value is null) return null;
        var v = Invisible().Replace(value, "").Trim();
        return v.Length == 0 ? null : v;
    }

    /// <summary>Why <paramref name="value"/> cannot be sent as <paramref name="field"/>, or null when it can (after <see cref="Clean"/>).</summary>
    public static string? Problem(string field, string? value) => Clean(value) switch
    {
        null => $"The {field} is empty.",
        var v when v.Any(char.IsWhiteSpace) => $"The {field} contains spaces; copy it again from the Paytm Money developer console.",
        var v when v[0] is '"' or '\'' || v[^1] is '"' or '\'' => $"The {field} is wrapped in quotes; save it without them.",
        _ => null,
    };

    /// <summary>The secret as authenticator apps show it ("jbsw y3dp ehpk 3pxp") in the Base32 form TOTP needs.</summary>
    public static string? NormalizeTotpSecret(string? value) =>
        Clean(value) is { } v ? Whitespace().Replace(v, "").ToUpperInvariant() : null;

    /// <summary>The fields of POST /accounts/v2/gettoken, cleaned; throws (not retried) when one cannot be sent.</summary>
    public static (string ApiKey, string ApiSecret, string RequestToken) ForTokenRequest(string? apiKey, string? apiSecret, string? requestToken)
    {
        var problems = new[] { Problem("API key", apiKey), Problem("API secret", apiSecret), Problem("request token", requestToken) }.OfType<string>().ToList();
        if (problems.Count > 0) throw new BrokerLoginException("Paytm token request not sent: " + string.Join(" ", problems));
        return (Clean(apiKey)!, Clean(apiSecret)!, Clean(requestToken)!);
    }

    [GeneratedRegex("[​-‍⁠﻿]")]
    private static partial Regex Invisible();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
