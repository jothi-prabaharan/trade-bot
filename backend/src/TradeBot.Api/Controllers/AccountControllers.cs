using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradeBot.Api.Hosting;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
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
    ConfigCache config, EngineOrchestrator orchestrator, TimeProvider clock) : ControllerBase
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

    [HttpGet("{id:long}/auth/login-url")]
    public async Task<IActionResult> LoginUrl(long id, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        if (a.ApiKey is null) return ApiResults.Invalid(RuleCodes.ApiCredentialsMissing, "Set the API key first.", "apiKey");
        return Ok(new { url = paytm.LoginUrl(a.ApiKey, $"tb{id}") });
    }

    public sealed record RequestTokenBody(string RequestToken);

    /// <summary>Manual login fallback: exchange the redirect's requestToken for API tokens.</summary>
    [HttpPost("{id:long}/auth/request-token"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> ExchangeRequestToken(long id, RequestTokenBody body, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is not { } a) return NotFound();
        if (a.ApiKey is null || a.ApiSecret is null) return ApiResults.Invalid(RuleCodes.ApiCredentialsMissing, "API key and secret are required.");
        if (string.IsNullOrWhiteSpace(body.RequestToken)) return ApiResults.Invalid("FIELD_REQUIRED", "requestToken is required.", "requestToken");

        var t = await paytm.GenerateTokensAsync(a.ApiKey, a.ApiSecret, body.RequestToken.Trim(), ct);
        var expiry = t.ExpiresUtc ?? Ist.ToUtc(Ist.Today(clock), new TimeOnly(23, 59));
        await accounts.AddTokenAsync(new AccountAuthToken
        {
            AccountId = id, XJwtToken = t.AccessToken, PublicAccessToken = t.PublicAccessToken, ReadAccessToken = t.ReadAccessToken,
            TokenExpiry = expiry, LoginStatus = LoginStatus.Success, LoginMethod = LoginMethod.Manual, AttemptCount = 1,
        }, ct);
        await bus.PublishAsync(EventChannels.TokensUpdated, id.ToString(), ct);
        await orchestrator.OnTokensUpdatedAsync(ct);
        return Ok(new { tokenExpiry = expiry });
    }

    /// <summary>Asks the Auth worker to run the headless TOTP login now.</summary>
    [HttpPost("{id:long}/auth/login"), EnableRateLimiting("mutations")]
    public async Task<IActionResult> TriggerLogin(long id, CancellationToken ct)
    {
        if (await accounts.GetAsync(id, ct) is null) return NotFound();
        await bus.PublishAsync(EventChannels.LoginRequested, id.ToString(), ct);
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
                case "apiKey": a.ApiKey = value; break;
                case "apiSecret": a.ApiSecret = value; break;
                case "totpSecret": a.TotpSecret = value?.Replace(" ", "").ToUpperInvariant(); break;
                case "loginPassword": a.LoginPassword = value; break;
                case "loginPin": a.LoginPin = value; break;
            }
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
