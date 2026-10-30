using TradeBot.Application.Abstractions;

namespace TradeBot.Application.Auth;

/// <summary>Paytm Money result codes in plain words, for logs, events, alerts and the dashboard.</summary>
public static class BrokerErrors
{
    public const string InvalidToken = "PM_MERCHANT_AUTH_401669";

    private const string FixTokenRequest =
        "check that the API key and API secret match the Paytm Money developer console exactly, and use a fresh request token: " +
        "each one works once, within 30 minutes of the login that produced it";

    private const string FixAccessToken = "the access token is invalid or expired; log in again";

    /// <summary>"Paytm: PM_MERCHANT_AUTH_400607 Invalid Api Key or Api Secret Key or Request Token (check that …)".</summary>
    public static string Describe(BrokerApiException e)
    {
        // 4006xx are the generate-access-token family (400607 = invalid API key, API secret or request token)
        var fix = e.ErrorCode switch
        {
            { } c when c.Equals(InvalidToken, StringComparison.OrdinalIgnoreCase) => FixAccessToken,
            { } c when c.StartsWith("PM_MERCHANT_AUTH_4006", StringComparison.OrdinalIgnoreCase) => FixTokenRequest,
            _ when e.TokenRejected => FixAccessToken,
            _ => null,
        };
        var code = e.ErrorCode ?? (e.StatusCode > 0 ? $"HTTP {e.StatusCode}" : "error");
        var said = string.IsNullOrWhiteSpace(e.BrokerMessage) ? code : $"{code} {e.BrokerMessage.Trim()}";
        return fix is null ? $"Paytm: {said}" : $"Paytm: {said} ({fix})";
    }
}
