using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("""{"access_token":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig","status":"ok"}""", """{"access_token":"[redacted]","status":"ok"}""")]
    [InlineData("""{"api_key":"k-123","api_secret_key":"s-456","request_token":"r-789"}""", """{"api_key":"[redacted]","api_secret_key":"[redacted]","request_token":"[redacted]"}""")]
    [InlineData("navigating to \"https://login.paytmmoney.com/merchant-login?apiKey=abc123&state=tb7\"", "navigating to \"https://login.paytmmoney.com/merchant-login?apiKey=[redacted]&state=tb7\"")]
    [InlineData("callback?requestToken=xyz&state=1", "callback?requestToken=[redacted]&state=1")]
    [InlineData("bearer eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjF9.abc_def in text", "bearer [redacted] in text")]
    [InlineData("""{"status":"FAILURE","message":"Invalid Api Key or Api Secret Key or Request Token","error_code":"PM_MERCHANT_AUTH_400607"}""",
        """{"status":"FAILURE","message":"Invalid Api Key or Api Secret Key or Request Token","error_code":"PM_MERCHANT_AUTH_400607"}""")]
    public void Credentials_are_hidden_and_paytm_messages_kept(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Fact]
    public void Known_secret_values_are_hidden_anywhere_but_short_ones_are_left_alone()
    {
        var text = SecretRedactor.Redact("secret S3cr3t-Value-99 and pin 1234 at 12:34", "S3cr3t-Value-99", "1234");
        Assert.Equal("secret [redacted] and pin 1234 at 12:34", text);
    }

    [Theory]
    [InlineData("abcdefghijkl1234", "••••1234")]
    [InlineData("short", "••••")]
    [InlineData(null, null)]
    public void Masks_keep_only_the_last_four_of_long_values(string? value, string? expected) => Assert.Equal(expected, SecretRedactor.Mask(value));
}

public class BrokerCredentialsTests
{
    [Theory]
    [InlineData("  key-123 \n", "key-123")]
    [InlineData("​key-123﻿", "key-123")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Pasted_values_are_trimmed_of_spaces_line_breaks_and_invisible_characters(string? input, string? expected) =>
        Assert.Equal(expected, BrokerCredentials.Clean(input));

    [Theory]
    [InlineData("abc def", "contains spaces")]
    [InlineData("\"abc\"", "wrapped in quotes")]
    [InlineData("", "is empty")]
    public void Values_that_cannot_be_sent_are_named(string value, string expected) =>
        Assert.Contains(expected, BrokerCredentials.Problem("API key", value));

    [Fact]
    public void The_token_request_gets_clean_values_or_is_not_sent()
    {
        Assert.Equal(("k", "s", "r"), BrokerCredentials.ForTokenRequest(" k ", "s\n", "\tr"));
        var e = Assert.Throws<BrokerLoginException>(() => BrokerCredentials.ForTokenRequest("k", null, "r r"));
        Assert.Contains("API secret is empty", e.Message);
        Assert.Contains("request token contains spaces", e.Message);
    }

    [Fact]
    public void Totp_secret_as_shown_by_authenticator_apps_becomes_base32() =>
        Assert.Equal("JBSWY3DPEHPK3PXP", BrokerCredentials.NormalizeTotpSecret(" jbsw y3dp\tehpk 3pxp "));
}

public class BrokerErrorsTests
{
    [Theory]
    [InlineData(400, "PM_MERCHANT_AUTH_400607", "Invalid Api Key or Api Secret Key or Request Token", "Paytm: PM_MERCHANT_AUTH_400607 Invalid Api Key or Api Secret Key or Request Token (check that the API key and API secret match")]
    [InlineData(400, "PM_MERCHANT_AUTH_400606", null, "Paytm: PM_MERCHANT_AUTH_400606 (check that the API key and API secret match")]
    [InlineData(401, "PM_MERCHANT_AUTH_401669", "Invalid Token", "Paytm: PM_MERCHANT_AUTH_401669 Invalid Token (the access token is invalid or expired; log in again)")]
    [InlineData(403, null, null, "Paytm: HTTP 403 (the access token is invalid or expired; log in again)")]
    [InlineData(500, null, "Internal error", "Paytm: HTTP 500 Internal error")]
    public void Codes_are_described_with_what_fixes_them(int status, string? code, string? message, string expected)
    {
        var e = new BrokerApiException("x", status, code, message, "{}");
        Assert.StartsWith(expected, BrokerErrors.Describe(e));
    }
}

public class TotpTests
{
    private const string Rfc = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Six_digit_codes_follow_rfc_6238_with_leading_zeros(long unix, string expected) =>
        Assert.Equal(expected, Totp.Compute(Rfc, DateTime.UnixEpoch.AddSeconds(unix)));

    [Fact]
    public void Ist_and_utc_give_the_same_code_for_the_same_instant()
    {
        var utc = new DateTimeOffset(2026, 11, 2, 3, 15, 10, TimeSpan.Zero);
        var ist = utc.ToOffset(TimeSpan.FromHours(5.5));
        Assert.Equal(Totp.Compute(Rfc, utc.UtcDateTime), Totp.Compute(Rfc, ist));
        // A local DateTime is converted, not read as UTC (which would be 5.5 h off on an IST server)
        var local = DateTime.SpecifyKind(utc.UtcDateTime, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(Totp.Compute(Rfc, utc.UtcDateTime), Totp.Compute(Rfc, local));
    }

    [Fact]
    public void Secrets_in_lowercase_groups_give_the_same_codes() =>
        Assert.Equal(Totp.Compute("JBSWY3DPEHPK3PXP", DateTime.UnixEpoch.AddSeconds(59)), Totp.Compute("jbsw y3dp ehpk 3pxp", DateTime.UnixEpoch.AddSeconds(59)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-30, -1)]
    [InlineData(30, 1)]
    [InlineData(-60, -2)]
    public void A_code_is_placed_relative_to_now(int codeTakenSecondsFromNow, int expectedStep)
    {
        var now = new DateTime(2026, 11, 2, 3, 15, 10, DateTimeKind.Utc);
        var code = Totp.Compute(Rfc, now.AddSeconds(codeTakenSecondsFromNow));
        Assert.Equal(expectedStep, Totp.MatchStep(Rfc, code, now));
    }

    [Fact]
    public void A_code_from_another_secret_matches_no_step()
    {
        var now = new DateTime(2026, 11, 2, 3, 15, 10, DateTimeKind.Utc);
        Assert.Null(Totp.MatchStep(Rfc, Totp.Compute("JBSWY3DPEHPK3PXP", now), now));
        Assert.Null(Totp.MatchStep(Rfc, "12345", now));
    }
}

public class BrokerSetupTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 3, 15, 25, DateTimeKind.Utc); // 25 s into the step

    private static TradingAccount Account(string? totp = "JBSWY3DPEHPK3PXP") => new()
    {
        AccountId = 7, AccountName = "Real", ClientId = "C7", UserName = "u", ApiKey = "abcdefgh-key-9f3a", ApiSecret = "secret-value",
        LoginId = "9876543210", LoginPassword = "pw", TotpSecret = totp,
    };

    [Fact]
    public void Ready_account_lists_every_item_ok_and_never_shows_secrets()
    {
        var items = BrokerSetup.Items(Account());
        Assert.All(items, i => Assert.True(i.Ok, i.Key));
        Assert.Equal("••••9f3a", items.Single(i => i.Key == "apiKey").Detail);
        Assert.Equal("98******10", items.Single(i => i.Key == "loginId").Detail);
        Assert.Contains("80-bit", items.Single(i => i.Key == "totpSecret").Detail);
        Assert.DoesNotContain(items, i => i.Detail.Contains("secret-value") || i.Detail.Contains("JBSWY3DP"));
    }

    [Fact]
    public void Problems_are_reported_per_item()
    {
        var a = Account(totp: "JBSWY3DP");
        a.ApiKey = "abc def";
        a.ApiSecret = null;
        var items = BrokerSetup.Items(a).ToDictionary(i => i.Key);
        Assert.Contains("contains spaces", items["apiKey"].Detail);
        Assert.Equal("Missing", items["apiSecret"].Detail);
        Assert.Contains("Too short", items["totpSecret"].Detail);
        Assert.False(items["totpSecret"].Ok);
    }

    [Fact]
    public void Authenticator_code_comparison_tells_secret_and_clock_problems_apart()
    {
        var a = Account();
        string At(int seconds) => Totp.Compute(a.TotpSecret!, Now.AddSeconds(seconds));
        Assert.Equal("MATCH", BrokerSetup.CompareTotp(a, At(0), Now).Result);
        Assert.Equal("CLOCK_OFF", BrokerSetup.CompareTotp(a, At(-30), Now).Result);
        Assert.Contains("ahead of your phone", BrokerSetup.CompareTotp(a, At(-30), Now).Message);
        Assert.Contains("behind your phone", BrokerSetup.CompareTotp(a, At(30), Now).Message);
        Assert.Equal("NO_MATCH", BrokerSetup.CompareTotp(a, Totp.Compute("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Now), Now).Result);
        Assert.Equal("BAD_CODE", BrokerSetup.CompareTotp(a, "12 34", Now).Result);
        Assert.Equal("NO_SECRET", BrokerSetup.CompareTotp(Account(totp: null), At(0), Now).Result);
        // Typed just as the code changed: still a match
        var justAfterStep = new DateTime(2026, 11, 2, 3, 15, 32, DateTimeKind.Utc);
        Assert.Equal("MATCH", BrokerSetup.CompareTotp(a, Totp.Compute(a.TotpSecret!, justAfterStep.AddSeconds(-30)), justAfterStep).Result);
    }
}

public class BrokerHealthMonitorTests
{
    [Fact]
    public async Task A_token_paytm_rejects_raises_one_alert_and_shows_as_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero));
        var accounts = Stub<IAccountRepository>.Create();
        var paytm = Stub<IPaytmMoneyClient>.Create();
        var trading = Stub<ITradingRepository>.Create();
        var alerts = Stub<IAlertSender>.Create();
        var account = WorkerFixture.Account(7);
        var token = new AccountAuthToken { AccountId = 7, XJwtToken = "jwt", LoginStatus = LoginStatus.Success, TokenExpiry = clock.GetUtcNow().UtcDateTime.AddHours(10), CreatedAt = clock.GetUtcNow().UtcDateTime.AddHours(-1) };
        accounts.On(nameof(IAccountRepository.ListAsync), _ => new List<TradingAccount> { account, WorkerFixture.Account(8) });
        accounts.On(nameof(IAccountRepository.LatestTokensAsync), _ => new Dictionary<long, AccountAuthToken> { [7] = token });
        paytm.On(nameof(IPaytmMoneyClient.GetProfileAsync), _ => Task.FromException<BrokerProfile>(new BrokerApiException("x", 401, "PM_MERCHANT_AUTH_401669", "Invalid Token", "{}")));
        var checker = new BrokerConnectionChecker(paytm.Object, clock, NullLogger<BrokerConnectionChecker>.Instance);
        var monitor = new BrokerHealthMonitor(accounts.Object, checker, trading.Object, alerts.Object, clock);

        Assert.Equal(1, await monitor.CheckAsync(null, CancellationToken.None));
        Assert.Equal(1, await monitor.CheckAsync(null, CancellationToken.None));

        // Account 8 has no token: never sent to Paytm; the rejection alerts once, not every 10 minutes
        Assert.Equal(2, paytm.Calls(nameof(IPaytmMoneyClient.GetProfileAsync)).Count);
        Assert.Contains("PM_MERCHANT_AUTH_401669", (string)Assert.Single(alerts.Calls(nameof(IAlertSender.SendAsync)))[1]!);
        Assert.Equal("TOKEN_REJECTED", ((SystemEvent)Assert.Single(trading.Calls(nameof(ITradingRepository.AddEventAsync)))[0]!).EventType);
        Assert.True(checker.Last(7)!.TokenRejected);
    }

    [Fact]
    public async Task A_healthy_token_reports_the_paytm_user()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero));
        var paytm = Stub<IPaytmMoneyClient>.Create();
        paytm.On(nameof(IPaytmMoneyClient.GetProfileAsync), _ => new BrokerProfile("c7", "Asha Trader", "asha@example.in", "9876543210", "{}"));
        paytm.On("get_Clock", _ => new BrokerClockReading(TimeSpan.FromSeconds(0.4), clock.GetUtcNow().UtcDateTime));
        var checker = new BrokerConnectionChecker(paytm.Object, clock, NullLogger<BrokerConnectionChecker>.Instance);

        var c = await checker.CheckAsync(WorkerFixture.Account(7), "jwt", null, CancellationToken.None);
        Assert.True(c.Ok);
        Assert.Equal(("c7", "Asha Trader", true, 0.4), (c.ClientId, c.Name, c.ClientIdMatches!.Value, c.ClockSkewSeconds!.Value));
        Assert.Equal("jwt", paytm.Calls(nameof(IPaytmMoneyClient.GetProfileAsync)).Single()[0]);

        var none = await checker.CheckAsync(WorkerFixture.Account(7), null, null, CancellationToken.None);
        Assert.Equal(("NO_TOKEN", false), (none.ErrorCode, none.Ok));

        // An HTML maintenance page instead of JSON is reported, not thrown (it must not fail a login that has tokens)
        paytm.On(nameof(IPaytmMoneyClient.GetProfileAsync), _ => Task.FromException<BrokerProfile>(new System.Text.Json.JsonException("'<' is an invalid start of a value.")));
        var garbled = await checker.CheckAsync(WorkerFixture.Account(7), "jwt", null, CancellationToken.None);
        Assert.Equal(("CHECK_FAILED", false, false), (garbled.ErrorCode, garbled.Ok, garbled.TokenRejected));
    }

    [Theory]
    [InlineData(42, "42 s behind Paytm's")]
    [InlineData(-15, "15 s ahead of Paytm's")]
    [InlineData(3, null)]
    public void Clock_warning_names_the_drift(int skewSeconds, string? expected)
    {
        var warning = BrokerConnectionChecker.ClockWarning(new BrokerClockReading(TimeSpan.FromSeconds(skewSeconds), DateTime.UtcNow));
        if (expected is null) Assert.Null(warning);
        else Assert.Contains(expected, warning);
    }
}

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.0.0+adfeb66cc830c1e37530fe43f4e4087fcf567ad7", "adfeb66")]
    [InlineData("1.0.0+abc", "abc")]
    [InlineData("1.0.0", "dev")]
    [InlineData(null, "dev")]
    public void Revision_is_the_short_commit_from_the_informational_version(string? version, string expected) =>
        Assert.Equal(expected, BuildInfo.Read(version));
}
