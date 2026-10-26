using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Auth;

/// <summary>
/// Daily Paytm login per account: headless TOTP login → requestToken → access/public/read tokens in
/// mst.account_auth_tokens. Retries, then records the failure and alerts with the manual login link.
/// </summary>
public sealed class BrokerAuthService(
    IAccountRepository accounts, IConfigRepository configs, IPaytmMoneyClient paytm, IBrokerLoginAutomation browser,
    ITradingRepository trading, IEventBus bus, IAlertSender alerts, TimeProvider clock, ILogger<BrokerAuthService> log)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Accounts that need broker tokens: REAL accounts and the market-data provider.</summary>
    public static bool NeedsLogin(TradingAccount a) =>
        a.IsActive && (a.TradeMode == TradeMode.Real || a.IsDataProvider) && a.ApiKey is not null && a.ApiSecret is not null;

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

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            tried = attempt;
            try
            {
                if (account.TotpSecret is null) throw new InvalidOperationException("TOTP secret not configured; headless login impossible.");
                var requestToken = await browser.GetRequestTokenAsync(account, loginUrl, ct);
                var t = await paytm.GenerateTokensAsync(account.ApiKey!, account.ApiSecret!, requestToken, ct);
                await accounts.AddTokenAsync(new AccountAuthToken
                {
                    AccountId = accountId, XJwtToken = t.AccessToken, PublicAccessToken = t.PublicAccessToken, ReadAccessToken = t.ReadAccessToken,
                    TokenExpiry = t.ExpiresUtc ?? Ist.ToUtc(Ist.Today(clock), new TimeOnly(23, 59)),
                    LoginStatus = LoginStatus.Success, LoginMethod = LoginMethod.Headless, AttemptCount = attempt,
                }, ct);
                await bus.PublishAsync(EventChannels.TokensUpdated, accountId.ToString(), ct);
                await trading.AddEventAsync(new SystemEvent
                {
                    AccountId = accountId, EventType = "LOGIN_SUCCESS", Severity = EventSeverity.Info,
                    Message = $"Paytm login succeeded for {account.AccountName} (attempt {attempt})",
                }, ct);
                log.LogInformation("Account {Account} logged in (attempt {Attempt})", accountId, attempt);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastError = e.Message;
                log.LogWarning(e, "Login attempt {Attempt}/{Attempts} failed for account {Account}", attempt, attempts, accountId);
                if (account.TotpSecret is null) break;
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
            $"🔐 Paytm login failed for <b>{System.Net.WebUtility.HtmlEncode(account.AccountName)}</b> after {tried} attempt(s): {System.Net.WebUtility.HtmlEncode(lastError ?? "")}\n" +
            $"Log in manually: {loginUrl}\nThen paste the requestToken on the dashboard.", ct);
        return false;
    }
}
