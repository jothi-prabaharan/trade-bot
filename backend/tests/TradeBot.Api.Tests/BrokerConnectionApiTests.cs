using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Api.Tests;

/// <summary>Paytm credentials, token exchange, user-details connection check and TOTP comparison through the API.</summary>
[Collection(ApiCollection.Name)]
public class BrokerConnectionApiTests(ApiFactory api)
{
    private const string Totp = "JBSWY3DPEHPK3PXP";

    /// <summary>A REAL-ready account whose secrets were pasted the way people paste them.</summary>
    private async Task<(HttpClient Client, long Id)> AccountAsync(string name, string clientId)
    {
        var client = await api.AuthorizedClientAsync();
        var r = await client.PostAsJsonAsync("/api/accounts", new
        {
            accountName = name, clientId, userName = "Asha Trader", loginId = "9876543210",
            apiKey = "  live-key-0001-9f3a \n", apiSecret = "​live-secret-0001 ", loginPassword = "pw-123",
            totpSecret = "jbsw y3dp ehpk 3pxp",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (client, (await r.Content.ReadFromJsonAsync<JsonObject>())!["accountId"]!.GetValue<long>());
    }

    [Fact]
    public async Task Pasted_keys_are_stored_trimmed_and_the_setup_check_shows_them_masked()
    {
        var (client, id) = await AccountAsync("Broker Setup", "PM-SETUP");
        await using (var db = await api.DbAsync())
        {
            var a = await db.Set<TradingAccount>().SingleAsync(x => x.AccountId == id);
            Assert.Equal(("live-key-0001-9f3a", "live-secret-0001", Totp), (a.ApiKey, a.ApiSecret, a.TotpSecret));
        }
        // Blanks typed into a stored secret's field keep the stored value
        var version = (await client.GetFromJsonAsync<JsonObject>($"/api/accounts/{id}"))!["version"]!.GetValue<uint>();
        (await client.PutAsJsonAsync($"/api/accounts/{id}", new { apiKey = "   ", version })).EnsureSuccessStatusCode();
        await using (var db = await api.DbAsync())
            Assert.Equal("live-key-0001-9f3a", (await db.Set<TradingAccount>().SingleAsync(x => x.AccountId == id)).ApiKey);

        api.Paytm.Clock = new BrokerClockReading(TimeSpan.FromSeconds(0.4), api.Clock.GetUtcNow().UtcDateTime);
        var setup = (await client.GetFromJsonAsync<JsonObject>($"/api/accounts/{id}/auth/setup"))!;
        var items = setup["items"]!.AsArray().ToDictionary(i => i!["key"]!.GetValue<string>(), i => i!);
        Assert.All(items.Values, i => Assert.True(i!["ok"]!.GetValue<bool>(), i!["key"]!.GetValue<string>()));
        Assert.Equal("••••9f3a", items["apiKey"]!["detail"]!.GetValue<string>());
        Assert.Equal(0.4, setup["clockSkewSeconds"]!.GetValue<double>());
        Assert.Null(setup["clockWarning"]);
        // The same instant shown in IST (UTC+05:30); TOTP itself only uses the UTC instant
        var utc = setup["serverTimeUtc"]!.GetValue<DateTime>().ToUniversalTime();
        Assert.Equal(utc.AddMinutes(330).ToString("yyyy-MM-dd HH:mm:ss"), setup["serverTimeIst"]!.GetValue<string>());
        Assert.DoesNotContain("live-secret", setup.ToJsonString());
    }

    [Fact]
    public async Task Totp_check_compares_with_the_authenticator_code_without_revealing_it()
    {
        var (client, id) = await AccountAsync("Broker Totp", "PM-TOTP");
        var now = api.Clock.GetUtcNow().UtcDateTime;
        async Task<JsonObject> Check(string code) =>
            (await (await client.PostAsJsonAsync($"/api/accounts/{id}/auth/totp-check", new { code })).Content.ReadFromJsonAsync<JsonObject>())!;

        var match = await Check(Application.Security.Totp.Compute(Totp, now));
        Assert.Equal("MATCH", match["result"]!.GetValue<string>());
        Assert.Equal("CLOCK_OFF", (await Check(Application.Security.Totp.Compute(Totp, now.AddSeconds(-60))))["result"]!.GetValue<string>());
        Assert.Equal("NO_MATCH", (await Check("000000" == Application.Security.Totp.Compute(Totp, now) ? "111111" : "000000"))["result"]!.GetValue<string>());
        Assert.DoesNotContain(Application.Security.Totp.Compute(Totp, now), match.ToJsonString());
    }

    [Fact]
    public async Task Manual_token_is_verified_with_user_details_before_it_is_stored()
    {
        var (client, id) = await AccountAsync("Broker Manual", "PM-MANUAL");
        try
        {
            // Paytm's own refusal comes back with its result code and what to do
            api.Paytm.Tokens = (_, _, _) => throw new BrokerApiException("x", 400, "PM_MERCHANT_AUTH_400607", "Invalid Api Key or Api Secret Key or Request Token", "{}");
            var refused = await client.PostAsJsonAsync($"/api/accounts/{id}/auth/request-token", new { requestToken = " req-1 " });
            var error = (await refused.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!;
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("PM_MERCHANT_AUTH_400607", error["code"]!.GetValue<string>());
            Assert.Contains("within 30 minutes", error["message"]!.GetValue<string>());
            Assert.Equal(("live-key-0001-9f3a", "live-secret-0001", " req-1 "), api.Paytm.TokenRequests[^1]);

            // Tokens Paytm then refuses on user details are not stored
            api.Paytm.Tokens = (_, _, _) => new BrokerTokens("bad-jwt", null, null, null);
            api.Paytm.Profile = _ => throw new BrokerApiException("x", 401, "PM_MERCHANT_AUTH_401669", "Invalid Token", "{}");
            var rejected = await client.PostAsJsonAsync($"/api/accounts/{id}/auth/request-token", new { requestToken = "req-2" });
            Assert.Equal("PM_MERCHANT_AUTH_401669", (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["code"]!.GetValue<string>());
            await using (var db = await api.DbAsync())
                Assert.False(await db.Set<AccountAuthToken>().AnyAsync(t => t.AccountId == id));

            api.Paytm.Tokens = (_, _, _) => new BrokerTokens("good-jwt", "public-jwt", "read-jwt", null);
            api.Paytm.Profile = _ => new BrokerProfile("PM-MANUAL", "Asha Trader", null, null, "{}");
            var ok = (await (await client.PostAsJsonAsync($"/api/accounts/{id}/auth/request-token", new { requestToken = "req-3" })).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("Asha Trader", ok["connection"]!["name"]!.GetValue<string>());
            Assert.True(ok["connection"]!["clientIdMatches"]!.GetValue<bool>());
            Assert.Contains("good-jwt", api.Paytm.ProfileTokens);
        }
        finally
        {
            api.Paytm.Tokens = (_, _, _) => new BrokerTokens("access-jwt", "public-jwt", "read-jwt", null);
            api.Paytm.Profile = _ => new BrokerProfile("PM12345", "Asha Trader", "asha@example.in", "9876543210", "{}");
        }
    }

    [Fact]
    public async Task Log_in_now_names_what_is_missing_instead_of_doing_nothing()
    {
        var client = await api.AuthorizedClientAsync();
        var created = await client.PostAsJsonAsync("/api/accounts", new
        {
            accountName = "Broker No Totp", clientId = "PM-NOTOTP", userName = "Asha Trader", loginId = "9876543210",
            apiKey = "live-key-0002", apiSecret = "live-secret-0002", loginPassword = "pw-123",
        });
        var noTotp = (await created.Content.ReadFromJsonAsync<JsonObject>())!["accountId"]!.GetValue<long>();
        var refused = await client.PostAsync($"/api/accounts/{noTotp}/auth/login", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("TOTP secret", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["message"]!.GetValue<string>());

        var (_, ready) = await AccountAsync("Broker Login Now", "PM-LOGINNOW");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/accounts/{ready}/auth/login", null)).StatusCode);
    }

    [Fact]
    public async Task The_bot_shows_its_totp_code_like_an_authenticator_app_and_logs_each_view()
    {
        var (client, id) = await AccountAsync("Broker Code", "PM-CODE");
        var r = await client.PostAsync($"/api/accounts/{id}/auth/totp-code", null);
        r.EnsureSuccessStatusCode();
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        var body = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var left = body["secondsRemaining"]!.GetValue<int>();
        Assert.Equal(Application.Security.Totp.Compute(Totp, now), body["code"]!.GetValue<string>());
        Assert.Equal(Application.Security.Totp.Compute(Totp, now.AddSeconds(left)), body["nextCode"]!.GetValue<string>());
        await using (var db = await api.DbAsync())
            Assert.True(await db.Set<SystemEvent>().AnyAsync(e => e.AccountId == id && e.EventType == "TOTP_CODE_SHOWN"));

        var setup = (await client.GetFromJsonAsync<JsonObject>($"/api/accounts/{id}/auth/setup"))!["autoLogin"]!;
        Assert.True(setup["enabled"]!.GetValue<bool>());
        Assert.Contains("logs in by itself at 08:45 IST", setup["note"]!.GetValue<string>());
        Assert.Equal("Tue 03 Nov, 08:45", setup["nextIst"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_url_carries_the_dashboards_nonce_in_state()
    {
        var (client, id) = await AccountAsync("Broker State", "PM-STATE");
        var url = (await client.GetFromJsonAsync<JsonObject>($"/api/accounts/{id}/auth/login-url?nonce=0123456789abcdef"))!["url"]!.GetValue<string>();
        Assert.EndsWith($"state=tb{id}z0123456789abcdef", url);
        var plain = (await client.GetFromJsonAsync<JsonObject>($"/api/accounts/{id}/auth/login-url?nonce=..%2Fx"))!["url"]!.GetValue<string>();
        Assert.EndsWith($"state=tb{id}", plain);
    }

    [Fact]
    public async Task An_inactive_account_picked_on_its_own_still_shows_but_stays_out_of_the_totals()
    {
        var (client, id) = await AccountAsync("Broker Inactive", "PM-INACTIVE");
        var version = (await client.GetFromJsonAsync<JsonObject>($"/api/accounts/{id}"))!["version"]!.GetValue<uint>();
        (await client.PutAsJsonAsync($"/api/accounts/{id}", new { isActive = false, version })).EnsureSuccessStatusCode();

        var single = (await client.GetFromJsonAsync<JsonObject>($"/api/dashboard/overview?scope={id}"))!["accounts"]!.AsArray();
        Assert.False(Assert.Single(single)!["isActive"]!.GetValue<bool>());
        var all = (await client.GetFromJsonAsync<JsonObject>("/api/dashboard/overview?scope=all"))!["accounts"]!.AsArray();
        Assert.DoesNotContain(all, a => a!["accountId"]!.GetValue<long>() == id);
    }

    [Fact]
    public async Task Connection_check_uses_todays_access_token_and_marks_a_rejected_one()
    {
        var (client, id) = await AccountAsync("Broker Check", "PM-CHECK");
        var none = (await (await client.PostAsync($"/api/accounts/{id}/auth/check", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("NO_TOKEN", none["errorCode"]!.GetValue<string>());

        await using (var db = await api.DbAsync())
        {
            db.Add(new AccountAuthToken
            {
                AccountId = id, XJwtToken = "today-jwt", LoginStatus = LoginStatus.Success, LoginMethod = LoginMethod.Manual, AttemptCount = 1,
                TokenExpiry = api.Clock.GetUtcNow().UtcDateTime.AddHours(8),
            });
            await db.SaveChangesAsync();
        }
        var ok = (await (await client.PostAsync($"/api/accounts/{id}/auth/check", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal(("PM12345", "Asha Trader"), (ok["clientId"]!.GetValue<string>(), ok["name"]!.GetValue<string>()));
        Assert.Equal("today-jwt", api.Paytm.ProfileTokens[^1]);

        try
        {
            api.Paytm.Profile = _ => throw new BrokerApiException("x", 401, "PM_MERCHANT_AUTH_401669", "Invalid Token", "{}");
            var bad = (await (await client.PostAsync($"/api/accounts/{id}/auth/check", null)).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.True(bad["tokenRejected"]!.GetValue<bool>());
            Assert.Contains("log in again", bad["error"]!.GetValue<string>());

            // The account card shows the rejection
            await api.Get<TokenStore>().ReloadAsync(CancellationToken.None);
            var card = (await client.GetFromJsonAsync<JsonObject>("/api/dashboard/overview"))!["accounts"]!.AsArray().First(a => a!["accountId"]!.GetValue<long>() == id)!;
            Assert.Equal("REJECTED", card["tokenStatus"]!.GetValue<string>());
            Assert.Equal("PM_MERCHANT_AUTH_401669", card["connection"]!["errorCode"]!.GetValue<string>());
        }
        finally
        {
            api.Paytm.Profile = _ => new BrokerProfile("PM12345", "Asha Trader", "asha@example.in", "9876543210", "{}");
        }
    }
}
