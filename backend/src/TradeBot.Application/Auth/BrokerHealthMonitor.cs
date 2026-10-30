using System.Collections.Concurrent;
using System.Net;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Auth;

/// <summary>
/// Re-checks today's tokens with GET /accounts/v1/user/details right after each login and every 10 minutes, so a token
/// Paytm stops accepting shows on the dashboard and raises one alert instead of surfacing as failed orders. Accounts the
/// bot can log in by itself (TOTP saved) are logged in again straight away.
/// </summary>
public sealed class BrokerHealthMonitor(
    IAccountRepository accounts, BrokerConnectionChecker connection, ITradingRepository trading, IAlertSender alerts, IEventBus bus, TimeProvider clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    // Token (by creation time) already alerted about, per account: one alert per rejected token
    private readonly ConcurrentDictionary<long, DateTime> _alerted = new();

    /// <summary>Checks every active account holding a token that is still valid today, or only <paramref name="accountId"/>.</summary>
    public async Task<int> CheckAsync(long? accountId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tokens = await accounts.LatestTokensAsync(ct);
        var checkedCount = 0;
        foreach (var a in await accounts.ListAsync(activeOnly: true, ct))
        {
            if (accountId is { } only && a.AccountId != only) continue;
            if (tokens.GetValueOrDefault(a.AccountId) is not { LoginStatus: LoginStatus.Success, XJwtToken.Length: > 0, TokenExpiry: { } expiry } t || expiry <= now) continue;
            var check = await connection.CheckAsync(a, t.XJwtToken, expiry, ct);
            checkedCount++;
            if (!check.TokenRejected || _alerted.TryGetValue(a.AccountId, out var seen) && seen == t.CreatedAt) continue;
            _alerted[a.AccountId] = t.CreatedAt;
            // Once per rejected token: a new token Paytm refuses as well fails that login, which stops there
            var relogin = BrokerAuthService.NeedsLogin(a) && BrokerAuthService.MissingForHeadless(a) is null;
            var text = $"Paytm no longer accepts today's token for {a.AccountName}: {check.Error}";
            var next = relogin ? "The bot is logging in again with the TOTP." : "Use Login now on the dashboard.";
            await trading.AddEventAsync(new SystemEvent { AccountId = a.AccountId, EventType = "TOKEN_REJECTED", Severity = EventSeverity.Critical, Message = $"{text} {next}" }, ct);
            await alerts.SendAsync(AlertKind.Critical, $"🔐 {WebUtility.HtmlEncode(text)}\n{next}", ct);
            if (relogin) await bus.PublishAsync(EventChannels.LoginRequested, a.AccountId.ToString(), ct);
        }
        return checkedCount;
    }
}
