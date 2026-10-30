using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Application.Auth;

/// <summary>
/// Connection health: GET /accounts/v1/user/details with the account's access token (x-jwt-token) proves Paytm accepts
/// the token and shows which Paytm user it belongs to. Keeps the latest result per account for the dashboard.
/// </summary>
public sealed class BrokerConnectionChecker(IPaytmMoneyClient paytm, TimeProvider clock, ILogger<BrokerConnectionChecker> log)
{
    /// <summary>Beyond this the TOTP code the server computes may already be the previous or next one.</summary>
    public static readonly TimeSpan ClockSkewLimit = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<long, BrokerConnectionCheck> _last = new();

    public BrokerConnectionCheck? Last(long accountId) => _last.GetValueOrDefault(accountId);

    public async Task<BrokerConnectionCheck> CheckAsync(TradingAccount account, string? accessToken, DateTime? tokenExpiryUtc, CancellationToken ct)
    {
        var result = await RunAsync(account, accessToken, tokenExpiryUtc, ct);
        _last[account.AccountId] = result;
        if (result.Ok)
            log.LogInformation("Paytm connection OK for account {Account}: user {Name}, client {ClientId}{Mismatch}", account.AccountId, result.Name,
                result.ClientId, result.ClientIdMatches == false ? $" (this account's client ID is {account.ClientId})" : "");
        else
            log.LogWarning("Paytm connection check failed for account {Account}: {Code} {Error}", account.AccountId, result.ErrorCode, result.Error);
        return result;
    }

    private async Task<BrokerConnectionCheck> RunAsync(TradingAccount account, string? accessToken, DateTime? expiry, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (string.IsNullOrEmpty(accessToken))
            return Failed(account, now, expiry, false, "NO_TOKEN", "No valid access token today: log in first.");
        try
        {
            var p = await paytm.GetProfileAsync(accessToken, ct);
            bool? matches = p.ClientId is { Length: > 0 } id && !string.IsNullOrWhiteSpace(account.ClientId)
                ? string.Equals(id.Trim(), account.ClientId.Trim(), StringComparison.OrdinalIgnoreCase)
                : null;
            return new BrokerConnectionCheck(account.AccountId, true, false, now, p.ClientId, p.Name, p.Email, p.Phone, matches, expiry, null, null, Skew());
        }
        catch (BrokerApiException e)
        {
            return Failed(account, now, expiry, e.TokenRejected, e.ErrorCode ?? (e.StatusCode > 0 ? $"HTTP_{e.StatusCode}" : "BROKER_ERROR"), BrokerErrors.Describe(e));
        }
        // Anything else is not a verdict on the token: report it, never let it fail a login that already has tokens
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return e is HttpRequestException or OperationCanceledException
                ? Failed(account, now, expiry, false, "UNREACHABLE", "Paytm did not answer; check the server's internet connection and retry.")
                : Failed(account, now, expiry, false, "CHECK_FAILED", $"Paytm's user details could not be read: {SecretRedactor.Redact(e.Message, accessToken)}");
        }
    }

    private BrokerConnectionCheck Failed(TradingAccount a, DateTime now, DateTime? expiry, bool rejected, string code, string error) =>
        new(a.AccountId, false, rejected, now, null, null, null, null, null, expiry, code, error, Skew());

    private double? Skew() => paytm.Clock is { } c ? Math.Round(c.Skew.TotalSeconds, 1) : null;

    /// <summary>"Server clock is 42 s behind Paytm" when the latest reading is past <see cref="ClockSkewLimit"/>, else null.</summary>
    public static string? ClockWarning(BrokerClockReading? reading) =>
        reading is { } r && r.Skew.Duration() >= ClockSkewLimit
            ? $"The server clock is {r.Skew.Duration().TotalSeconds:0} s {(r.Skew > TimeSpan.Zero ? "behind" : "ahead of")} Paytm's, so TOTP codes can be rejected: " +
              "turn on time sync (sudo timedatectl set-ntp true; chrony is installed by install.sh)."
            : null;
}
