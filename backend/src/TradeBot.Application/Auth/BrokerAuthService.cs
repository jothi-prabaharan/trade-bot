using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Auth;

/// <summary>
/// Daily Paytm login per account: headless TOTP login → requestToken → access/public/read tokens, verified against
/// GET /accounts/v1/user/details before they are stored in mst.account_auth_tokens. Retries, then records the failure
/// and alerts with the manual login link. Errors are redacted before they are logged, stored or sent.
/// </summary>
public sealed class BrokerAuthService(
    IAccountRepository accounts, IConfigRepository configs, IPaytmMoneyClient paytm, IBrokerLoginAutomation browser,
    BrokerConnectionChecker connection, ITradingRepository trading, IEventBus bus, IAlertSender alerts, TimeProvider clock,
    ILogger<BrokerAuthService> log)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Accounts that need broker tokens: REAL accounts and the market-data provider.</summary>
    public static bool NeedsLogin(TradingAccount a) =>
        a.IsActive && (a.TradeMode == TradeMode.Real || a.IsDataProvider) && a.ApiKey is not null && a.ApiSecret is not null;

    /// <summary>Why headless login cannot run for this account, or null when it has everything it needs.</summary>
    public static string? MissingForHeadless(TradingAccount a)
    {
        var missing = new List<string>();
        // Surrounding spaces are trimmed when sent; spaces or quotes inside mean the value was pasted wrongly
        foreach (var (field, value) in new[] { ("API key", a.ApiKey), ("API secret", a.ApiSecret) })
            if (BrokerCredentials.Clean(value) is null) missing.Add(field);
            else if (BrokerCredentials.Problem(field, value) is not null) missing.Add($"a clean {field} (no spaces or quotes inside)");
        if (string.IsNullOrWhiteSpace(a.LoginId ?? a.Phone ?? a.Email)) missing.Add("login ID (mobile or email)");
        if (string.IsNullOrEmpty(a.LoginPassword)) missing.Add("login password");
        if (string.IsNullOrWhiteSpace(a.TotpSecret)) missing.Add("TOTP secret");
        else
        {
            try { if (Totp.Base32Decode(a.TotpSecret).Length < 10) missing.Add("a valid TOTP secret (too short)"); }
            catch (FormatException) { missing.Add("a valid TOTP secret (not Base32)"); }
        }
        return missing.Count == 0 ? null
            : $"Headless login needs {string.Join(", ", missing)} in the account profile; nothing was sent to Paytm.";
    }

    public async Task LoginAllAsync(CancellationToken ct)
    {
        foreach (var a in (await accounts.ListAsync(activeOnly: true, ct)).Where(NeedsLogin))
            await LoginAsync(a.AccountId, ct);
    }

    public async Task<bool> LoginAsync(long accountId, CancellationToken ct)
    {
        if (await accounts.GetAsync(accountId, ct) is not { } account || !NeedsLogin(account)) return false;
        var strategy = await configs.StrategyAsync(ct);
        var attempts = Math.Max(1, strategy.AuthMaxRetries + 1);
        var loginUrl = paytm.LoginUrl(account.ApiKey!, $"tb{accountId}");
        string? lastError = null;
        var tried = 0;
        // A drifting server clock makes Paytm reject the TOTP; say so up front rather than after a failed attempt
        var clockWarning = BrokerConnectionChecker.ClockWarning(await paytm.ProbeClockAsync(ct));
        if (clockWarning is not null) log.LogWarning("Account {Account}: {Warning}", accountId, clockWarning);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            tried = attempt;
            try
            {
                // Checked before a browser opens: a half-run login can already have made Paytm send an SMS OTP
                if (MissingForHeadless(account) is { } missing) throw new BrokerLoginException(missing);
                var requestToken = await browser.GetRequestTokenAsync(account, loginUrl, ct);
                var t = await paytm.GenerateTokensAsync(account.ApiKey!, account.ApiSecret!, requestToken, ct);
                var expiry = t.ExpiresUtc ?? Ist.ToUtc(Ist.Today(clock), new TimeOnly(23, 59));
                var check = await connection.CheckAsync(account, t.AccessToken, expiry, ct);
                // A token Paytm refuses straight after issuing it points at the API app, not the login: do not loop
                if (check.TokenRejected) throw new BrokerLoginException($"Paytm issued tokens but {PaytmUserDetails} rejected them. {check.Error}");
                await accounts.AddTokenAsync(new AccountAuthToken
                {
                    AccountId = accountId, XJwtToken = t.AccessToken, PublicAccessToken = t.PublicAccessToken, ReadAccessToken = t.ReadAccessToken,
                    TokenExpiry = expiry, LoginStatus = LoginStatus.Success, LoginMethod = LoginMethod.Headless, AttemptCount = attempt,
                }, ct);
                await bus.PublishAsync(EventChannels.TokensUpdated, accountId.ToString(), ct);
                await trading.AddEventAsync(new SystemEvent
                {
                    AccountId = accountId, EventType = "LOGIN_SUCCESS", Severity = EventSeverity.Info,
                    Message = $"Paytm login succeeded for {account.AccountName} (attempt {attempt}); {Verified(check)}",
                }, ct);
                if (check.ClientIdMatches == false) await WarnClientMismatchAsync(account, check, ct);
                log.LogInformation("Account {Account} logged in (attempt {Attempt}); {Verified}", accountId, attempt, Verified(check));
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Playwright errors quote the login URL (API key) and Paytm bodies can echo tokens
                lastError = SecretRedactor.Redact(e.Message, account.ApiKey, account.ApiSecret, account.LoginPassword, account.TotpSecret);
                if (clockWarning is not null && e is BrokerLoginException && lastError.Contains("TOTP", StringComparison.Ordinal)) lastError += " " + clockWarning;
                log.LogWarning("Login attempt {Attempt}/{Attempts} failed for account {Account} ({Type}): {Error}",
                    attempt, attempts, accountId, e.GetType().Name, lastError);
                if (e is BrokerLoginException) break;
                if (attempt < attempts) await Task.Delay(RetryDelay, clock, ct);
            }
        }

        await accounts.AddTokenAsync(new AccountAuthToken
        {
            AccountId = accountId, LoginStatus = LoginStatus.Failed, LoginMethod = LoginMethod.Headless, AttemptCount = tried,
            ErrorMessage = lastError is { Length: > 1000 } ? lastError[..1000] : lastError,
        }, ct);
        await bus.PublishAsync(EventChannels.TokensUpdated, accountId.ToString(), ct);
        await trading.AddEventAsync(new SystemEvent
        {
            AccountId = accountId, EventType = "LOGIN_FAILED", Severity = EventSeverity.Critical,
            Message = $"Paytm login failed for {account.AccountName} after {tried} attempt(s): {lastError}" is var m && m.Length > 1000 ? m[..1000] : m,
        }, ct);
        await alerts.SendAsync(AlertKind.Critical,
            $"🔐 Paytm login failed for <b>{System.Net.WebUtility.HtmlEncode(account.AccountName)}</b> after {tried} attempt(s) (build {BuildInfo.Revision}): " +
            $"{System.Net.WebUtility.HtmlEncode(lastError ?? "")}\n" +
            $"Log in manually: {loginUrl}\nThen paste the requestToken on the dashboard.", ct);
        return false;
    }

    private const string PaytmUserDetails = "GET /accounts/v1/user/details";

    private static string Verified(BrokerConnectionCheck c) => c.Ok
        ? $"verified with {PaytmUserDetails} as {c.Name ?? "?"} (client {c.ClientId ?? "?"})"
        : $"could not verify with {PaytmUserDetails}: {c.Error}";

    /// <summary>The token works but belongs to another Paytm user than this account's client ID: orders would go there.</summary>
    private async Task WarnClientMismatchAsync(TradingAccount account, BrokerConnectionCheck check, CancellationToken ct)
    {
        var text = $"Paytm says this login is client {check.ClientId} ({check.Name}), but {account.AccountName} is set up as client {account.ClientId}. " +
                   "Check the login ID and API key in the account profile before trading.";
        await trading.AddEventAsync(new SystemEvent { AccountId = account.AccountId, EventType = "LOGIN_CLIENT_MISMATCH", Severity = EventSeverity.Critical, Message = text }, ct);
        await alerts.SendAsync(AlertKind.Critical, "⚠️ " + System.Net.WebUtility.HtmlEncode(text), ct);
    }
}
