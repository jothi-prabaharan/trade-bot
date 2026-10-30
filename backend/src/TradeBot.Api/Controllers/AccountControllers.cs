using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradeBot.Api.Hosting;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Application.Engine;
using TradeBot.Application.Security;
using TradeBot.Application.Validation;
using TradeBot.Application.Validation.Forms;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Api.Controllers;

public sealed record AccountDto(
    long AccountId, string AccountName, string ClientId, string UserName, string? Email, string? Phone, string? BrokerBranch, string? LoginId,
    TradeMode TradeMode, bool IsDataProvider, bool IsActive, bool HasApiKey, bool HasApiSecret, bool HasTotpSecret, bool HasLoginPassword, bool HasLoginPin,
    DateTime? RealModeEnabledAt, uint Version)
{
    public static AccountDto From(TradingAccount a) => new(a.AccountId, a.AccountName, a.ClientId, a.UserName, a.Email, a.Phone, a.BrokerBranch, a.LoginId,
        a.TradeMode, a.IsDataProvider, a.IsActive, !string.IsNullOrEmpty(a.ApiKey), !string.IsNullOrEmpty(a.ApiSecret), !string.IsNullOrEmpty(a.TotpSecret),
        !string.IsNullOrEmpty(a.LoginPassword), !string.IsNullOrEmpty(a.LoginPin), a.RealModeEnabledAt, a.Version);
}

internal static class Json
{
    public static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    public static bool Has(JsonObject o, string key) => o.ContainsKey(key);

    /// <summary>Entity → camelCase JSON with the request body's keys overlaid (full-record validation of partial updates).</summary>
    public static JsonObject Merge<T>(T entity, JsonObject body)
    {
        var merged = JsonSerializer.SerializeToNode(entity, FormValidator.EntityJson)!.AsObject();
        foreach (var (k, v) in body) merged[k] = v?.DeepClone();
        return merged;
    }
}

[ApiController, Authorize, Route("api/accounts")]
public sealed class AccountController(
    IAccountRepository accounts, ITradingRepository trading, TradeManager trades, IPaytmMoneyClient paytm, IEventBus bus,
    ConfigCache config, EngineOrchestrator orchestrator, OtpPrompts otp, BrokerConnectionChecker connection, TimeProvider clock) : ControllerBase
{
    private static readonly string[] Secrets = ["apiKey", "apiSecret", "totpSecret", "loginPassword", "loginPin"];

    [HttpGet]
    public async Task<IEnumerable<AccountDto>> List(CancellationToken ct) => (await accounts.ListAsync(false, ct)).Select(AccountDto.From);

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => await accounts.GetAsync(id, ct) is { } a ? Ok(AccountDto.From(a)) : NotFound();

    /// <summary>New accounts always start in PAPER; REAL needs the confirmed mode toggle.</summary>
    [HttpPost, EnableRateLimiting("mutations")]
    public async Task<IActionResult> Create([FromBody] JsonObject body, CancellationToken ct)
    {
        NormalizeCredentials(body);
        var candidate = new TradingAccount { AccountName = "", ClientId = "", UserName = "" };
        var merged = Json.Merge(candidate, body);
        if (FormValidator.Validate(FormRuleCatalog.TradingAccount, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);
        Apply(candidate, merged, body);
        candidate.TradeMode = TradeMode.Paper;
        candidate.RiskConfig = new AccountRiskConfig();
        var created = await accounts.AddAsync(candidate, ct);
        await ReloadAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = created.AccountId }, AccountDto.From(created));
    }

    [HttpPut("{id:long}"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> Update(long id, [FromBody] JsonObject body, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } existing) return NotFound();
        NormalizeCredentials(body);
        var merged = Json.Merge(existing, body);
        merged["hasApiKey"] = !string.IsNullOrEmpty(existing.ApiKey);
        merged["hasApiSecret"] = !string.IsNullOrEmpty(existing.ApiSecret);
        if (FormValidator.Validate(FormRuleCatalog.TradingAccount, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);
        if (body["version"] is JsonValue v && v.TryGetValue<uint>(out var version)) existing.Version = version;
        Apply(existing, merged, body);
        await accounts.UpdateAsync(existing, ct);
        await ReloadAsync(ct);
        return Ok(AccountDto.From((await accounts.GetAsync(id, ct))!));
    }

    public sealed record ModeRequest(string TargetMode, string? ConfirmClientId);

    /// <summary>Live PAPER↔REAL toggle; only while the account is flat, REAL needs the typed client ID.</summary>
    [HttpPost("{id:long}/mode"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> SetMode(long id, ModeRequest request, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } account) return NotFound();
        var form = new JsonObject { ["targetMode"] = request.TargetMode, ["confirmClientId"] = request.ConfirmClientId, ["accountClientId"] = account.ClientId };
        if (FormValidator.Validate(FormRuleCatalog.ModeToggle, form) is { Count: > 0 } formErrors) return ApiResults.Invalid(formErrors);

        var target = DbValue.FromDb<TradeMode>(request.TargetMode);
        var open = Math.Max(await trading.OpenTradeCountAsync(id, ct), trades.OpenTrades.Count(t => t.Account.AccountId == id) + trades.WorkingEntries(id));
        if (ModeToggleValidator.Validate(account, open, target, request.ConfirmClientId) is { Count: > 0 } errors) return ApiResults.Invalid(errors);

        var by = User.Identity?.Name ?? "admin";
        await accounts.SetTradeModeAsync(id, target, by, clock.GetUtcNow().UtcDateTime, ct);
        await trading.AddEventAsync(new Domain.Entities.Trn.SystemEvent { AccountId = id, EventType = "MODE_CHANGE", Severity = EventSeverity.Warning, Message = $"{by} switched account to {request.TargetMode}" }, ct);
        await ReloadAsync(ct);
        return Ok(AccountDto.From((await accounts.GetAsync(id, ct))!));
    }

    /// <param name="nonce">
    /// The dashboard's own random value (16–32 hex characters). It rides in Paytm's state (tb{id}z{nonce}) and comes back
    /// with the requestToken, so the page Paytm returns to can tell a login it started from a link someone else made.
    /// </param>
    [HttpGet("{id:long}/auth/login-url")]
    public async Task<IActionResult> LoginUrl(long id, [FromQuery] string? nonce, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        if (a.ApiKey is null) return ApiResults.Invalid(RuleCodes.ApiCredentialsMissing, "Set the API key first.", "apiKey");
        var state = nonce is not null && Regex.IsMatch(nonce, "^[0-9a-f]{16,32}$") ? $"tb{id}z{nonce}" : $"tb{id}";
        return Ok(new { url = paytm.LoginUrl(a.ApiKey, state) });
    }

    public sealed record RequestTokenBody(string RequestToken);

    /// <summary>
    /// Manual login fallback: exchange the redirect's requestToken for API tokens (POST /accounts/v2/gettoken), confirm
    /// them with GET /accounts/v1/user/details, then store them. Paytm's result code comes back as the error code.
    /// </summary>
    [HttpPost("{id:long}/auth/request-token"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> ExchangeRequestToken(long id, RequestTokenBody body, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        if (BrokerCredentials.Clean(a.ApiKey) is null || BrokerCredentials.Clean(a.ApiSecret) is null)
            return ApiResults.Invalid(RuleCodes.ApiCredentialsMissing, "API key and secret are required.");
        if (BrokerCredentials.Clean(body.RequestToken) is null) return ApiResults.Invalid("FIELD_REQUIRED", "requestToken is required.", "requestToken");

        BrokerTokens t;
        try { t = await paytm.GenerateTokensAsync(a.ApiKey!, a.ApiSecret!, body.RequestToken, ct); }
        catch (BrokerApiException e) { return ApiResults.Invalid(e.ErrorCode ?? "BROKER_ERROR", BrokerErrors.Describe(e), "requestToken"); }
        catch (BrokerLoginException e) { return ApiResults.Invalid(RuleCodes.ApiCredentialsMissing, e.Message, "requestToken"); }

        var expiry = t.ExpiresUtc ?? Ist.ToUtc(Ist.Today(clock), new TimeOnly(23, 59));
        var check = await connection.CheckAsync(a, t.AccessToken, expiry, ct);
        if (check.TokenRejected) return ApiResults.Invalid(check.ErrorCode ?? "TOKEN_REJECTED", $"Paytm issued tokens but rejected them on user details. {check.Error}", "requestToken");
        await accounts.AddTokenAsync(new AccountAuthToken
        {
            AccountId = id, XJwtToken = t.AccessToken, PublicAccessToken = t.PublicAccessToken, ReadAccessToken = t.ReadAccessToken,
            TokenExpiry = expiry, LoginStatus = LoginStatus.Success, LoginMethod = LoginMethod.Manual, AttemptCount = 1,
        }, ct);
        await bus.PublishAsync(EventChannels.TokensUpdated, id.ToString(), ct);
        await orchestrator.OnTokensUpdatedAsync(ct);
        return Ok(new { tokenExpiry = expiry, connection = check });
    }

    /// <summary>Connection health: GET /accounts/v1/user/details with today's access token (x-jwt-token).</summary>
    [HttpPost("{id:long}/auth/check"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> CheckConnection(long id, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        var token = (await accounts.LatestTokensAsync(ct)).GetValueOrDefault(id);
        var valid = token is { LoginStatus: LoginStatus.Success, XJwtToken.Length: > 0, TokenExpiry: { } exp } && exp > clock.GetUtcNow().UtcDateTime;
        return Ok(await connection.CheckAsync(a, valid ? token!.XJwtToken : null, token?.TokenExpiry, ct));
    }

    /// <summary>Headless-login readiness without contacting Paytm's login: credentials as they will be sent, TOTP secret, clocks.</summary>
    [HttpGet("{id:long}/auth/setup")]
    public async Task<IActionResult> Setup(long id, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        var reading = await paytm.ProbeClockAsync(ct) ?? paytm.Clock;
        var now = clock.GetUtcNow().UtcDateTime;
        var latest = (await accounts.LatestTokensAsync(ct)).GetValueOrDefault(id);
        return Ok(new BrokerSetupReport(id, BrokerSetup.Items(a), now, FixedLimits.ToIst(now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            reading is null ? null : Math.Round(reading.Skew.TotalSeconds, 1), BrokerConnectionChecker.ClockWarning(reading),
            Totp.SecondsRemaining(now), connection.Last(id), BrokerSetup.AutoLogin(a, latest, now, config.Strategy.AuthLoginTime, config.Calendar)));
    }

    public sealed record TotpCheckBody(string? Code);

    /// <summary>Compares the server's TOTP with the code in a phone authenticator app holding the same secret; no code is sent back.</summary>
    [HttpPost("{id:long}/auth/totp-check"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> CheckTotp(long id, TotpCheckBody body, CancellationToken ct) =>
        await accounts.GetAsync(id, ct) is { } a ? Ok(BrokerSetup.CompareTotp(a, body.Code, clock.GetUtcNow().UtcDateTime)) : NotFound();

    /// <summary>
    /// The bot as the account's authenticator app: the current TOTP code and the next one, for Paytm's TOTP setup and the
    /// owner's own Paytm logins. Never cached; each view is recorded in the event log.
    /// </summary>
    [HttpPost("{id:long}/auth/totp-code"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> TotpCode(long id, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        if (BrokerSetup.TotpProblem(a.TotpSecret) is { } problem) return ApiResults.Invalid("TOTP_SECRET", problem, "totpSecret");
        var now = clock.GetUtcNow().UtcDateTime;
        var left = Totp.SecondsRemaining(now);
        await trading.AddEventAsync(new Domain.Entities.Trn.SystemEvent
        {
            AccountId = id, EventType = "TOTP_CODE_SHOWN", Severity = EventSeverity.Info,
            Message = $"{User.Identity?.Name ?? "admin"} viewed the TOTP code for {a.AccountName} on the dashboard",
        }, ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { code = Totp.Compute(a.TotpSecret!, now), secondsRemaining = left, nextCode = Totp.Compute(a.TotpSecret!, now.AddSeconds(left)) });
    }

    /// <summary>Asks the Auth worker to run the headless TOTP login now; refused with the reason when it cannot run.</summary>
    [HttpPost("{id:long}/auth/login"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> TriggerLogin(long id, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        if (!a.IsActive) return ApiResults.Invalid("ACCOUNT_INACTIVE", $"{a.AccountName} is inactive: activate it before logging it in.");
        if (BrokerAuthService.MissingForHeadless(a) is { } missing) return ApiResults.Invalid(RuleCodes.ApiCredentialsMissing, missing);
        await bus.PublishAsync(EventChannels.LoginRequested, id.ToString(), ct);
        return Accepted();
    }

    public sealed record OtpBody(string Otp);

    /// <summary>The SMS OTP Paytm sent, typed on the dashboard while the Auth worker holds the login page open.</summary>
    [HttpPost("{id:long}/auth/otp"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> SubmitOtp(long id, OtpBody body, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is null) return NotFound();
        var code = body.Otp?.Trim() ?? "";
        if (!SmsOtpRelay.IsOtp(code)) return ApiResults.Invalid("OTP_FORMAT", "Enter the 4 to 8 digit OTP from the SMS.", "otp");
        if (otp.For(id) is null) return ApiResults.Invalid("OTP_NOT_PENDING", "The login is not waiting for an OTP any more. Start the login again.", "otp");
        await bus.PublishAsync(EventChannels.OtpSubmitted, $"{id}:{code}", ct);
        otp.OnClosed(id.ToString());
        return Accepted();
    }

    public sealed record KillRequest(string Reason);

    [HttpPost("{id:long}/kill-switch"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> KillSwitch(long id, KillRequest request, CancellationToken ct)
    {
        if (config.Account(id) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiResults.Invalid("FIELD_REQUIRED", "A reason is required.", "reason");
        await trades.KillSwitchAsync(id, User.Identity?.Name ?? "admin", request.Reason, ct);
        return Accepted();
    }

    private static void Apply(TradingAccount a, JsonObject merged, JsonObject body)
    {
        a.AccountName = Json.Str(merged, "accountName")!;
        a.ClientId = Json.Str(merged, "clientId")!;
        a.UserName = Json.Str(merged, "userName")!;
        a.Email = Json.Str(merged, "email");
        a.Phone = Json.Str(merged, "phone");
        a.BrokerBranch = Json.Str(merged, "brokerBranch");
        a.LoginId = Json.Str(merged, "loginId");
        a.IsDataProvider = merged["isDataProvider"]?.GetValue<bool>() ?? a.IsDataProvider;
        a.IsActive = merged["isActive"]?.GetValue<bool>() ?? a.IsActive;
        // Secrets are write-only: only keys present in the body change them
        foreach (var key in Secrets.Where(k => Json.Has(body, k)))
        {
            var value = Json.Str(body, key);
            switch (key)
            {
                case "apiKey": a.ApiKey = BrokerCredentials.Clean(value); break;
                case "apiSecret": a.ApiSecret = BrokerCredentials.Clean(value); break;
                case "totpSecret": a.TotpSecret = BrokerCredentials.NormalizeTotpSecret(value); break;
                case "loginPassword": a.LoginPassword = value; break;
                case "loginPin": a.LoginPin = value; break;
            }
        }
    }

    /// <summary>
    /// Before validation: keys pasted with spaces or line breaks are trimmed, and a TOTP secret copied as authenticator apps
    /// show it ("jbsw y3dp …") becomes the Base32 the rule expects.
    /// </summary>
    private static void NormalizeCredentials(JsonObject body)
    {
        // Only blanks typed: keep what is stored (an explicit null still clears it)
        foreach (var key in new[] { "apiKey", "apiSecret", "totpSecret" })
        {
            if (body[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
            var clean = key == "totpSecret" ? BrokerCredentials.NormalizeTotpSecret(s) : BrokerCredentials.Clean(s);
            if (clean is null) body.Remove(key);
            else body[key] = clean;
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await config.ReloadAsync(ct);
        await bus.PublishAsync(EventChannels.ConfigChanged, "trading_accounts", ct);
    }
}

[ApiController, Authorize, Route("api/accounts/{id:long}/risk-config")]
public sealed class RiskConfigController(IAccountRepository accounts, ConfigCache config) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        await accounts.GetAsync(id, ct) is { RiskConfig: { } r } ? Ok(WithoutAccount(r)) : NotFound();

    /// <summary>Validated against the same riskConfig form rules as the dashboard drawer (fixed bounds = DB CHECKs).</summary>
    [HttpPut, EnableRateLimiting("mutations")]
    public async Task<IActionResult> Update(long id, [FromBody] JsonObject body, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { RiskConfig: { } current } account) return NotFound();
        current.Account = null!;
        var merged = Json.Merge(current, body);
        if (FormValidator.Validate(FormRuleCatalog.RiskConfig, merged) is { Count: > 0 } errors) return ApiResults.Invalid(errors);

        var next = merged.Deserialize<AccountRiskConfig>(FormValidator.EntityJson)!;
        current.AllocatedCapital = next.AllocatedCapital;
        current.RiskPerTradePct = next.RiskPerTradePct;
        current.MinSingleTradePrice = next.MinSingleTradePrice;
        current.MaxSingleTradePrice = next.MaxSingleTradePrice;
        current.DefaultSlPct = next.DefaultSlPct;
        current.DefaultTargetPct = next.DefaultTargetPct;
        current.MinRiskRewardRatio = next.MinRiskRewardRatio;
        current.MaxSlPct = next.MaxSlPct;
        current.BreakevenTriggerPct = next.BreakevenTriggerPct;
        current.TrailingSlStepPct = next.TrailingSlStepPct;
        current.MaxDailyLoss = next.MaxDailyLoss;
        current.MaxDailyProfit = next.MaxDailyProfit;
        current.MaxOpenTrades = next.MaxOpenTrades;
        current.MaxLotsPerTrade = next.MaxLotsPerTrade;
        current.MaxTradesPerDay = next.MaxTradesPerDay;
        current.ReentryCooldownMinutes = next.ReentryCooldownMinutes;
        current.LotMultiplier = next.LotMultiplier;
        current.MinPatternConfidence = next.MinPatternConfidence;
        current.BlockTradeOnOpposingNews = next.BlockTradeOnOpposingNews;
        current.SlippageBufferPct = next.SlippageBufferPct;
        current.SimulatedSlippagePct = next.SimulatedSlippagePct;
        current.SimulatedBrokeragePerOrder = next.SimulatedBrokeragePerOrder;
        if (account.TradeMode == TradeMode.Paper)
        {
            // Virtual capital is user-managed; REAL margins come from the broker
            current.OpeningBalance = next.OpeningBalance;
            current.AvailableMargin = next.AvailableMargin;
        }
        await accounts.UpdateRiskConfigAsync(current, ct);
        await config.ReloadAsync(ct);
        return Ok(current);
    }

    private static AccountRiskConfig WithoutAccount(AccountRiskConfig r)
    {
        r.Account = null!;
        return r;
    }
}
