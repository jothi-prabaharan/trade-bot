using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Infrastructure.External;

namespace TradeBot.Infrastructure.Tests;

/// <summary>
/// A Paytm-style login site: mobile → password → second factor → redirect with requestToken. Counts the SMS OTPs it
/// "sends" so tests can prove the automation never makes Paytm text the user again. Like Paytm, it challenges an unknown
/// browser with an SMS OTP and remembers a device whose "Trust this device" box was ticked.
/// </summary>
internal sealed class FakeLoginSite : IDisposable
{
    public const string Secret = "JBSWY3DPEHPK3PXP";
    public const string SmsCode = "482913";
    private readonly HttpListener _http = new();
    private int _otpsSent;
    public string Root { get; }
    public int OtpsSent => _otpsSent;
    public List<string> Codes { get; } = [];

    public FakeLoginSite()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Root = $"http://127.0.0.1:{port}";
        _http.Prefixes.Add(Root + "/");
        _http.Start();
        _ = Task.Run(LoopAsync);
    }

    private sealed record Reply(int Status, string Html, string? Location = null, string? Cookie = null);

    private async Task LoopAsync()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            var q = ctx.Request.QueryString;
            var trusted = ctx.Request.Cookies["device"]?.Value == "trusted";
            var r = Route(ctx.Request.Url!.AbsolutePath, q["mode"] ?? "totp", q, trusted);
            ctx.Response.StatusCode = r.Status;
            if (r.Location is not null) ctx.Response.RedirectLocation = r.Location;
            if (r.Cookie is not null) ctx.Response.AppendHeader("Set-Cookie", r.Cookie);
            var bytes = Encoding.UTF8.GetBytes(r.Html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private Reply Route(string path, string mode, System.Collections.Specialized.NameValueCollection q, bool trusted) => path switch
    {
        "/login" when mode == "session" && trusted => new(302, "", "/callback?requestToken=tok-123"),
        "/login" => Ok(MobilePage(mode, mode is "otplogin" or "otponly" ? "/after-id-otp" : "/after-id",
            mode == "otplogin" ? $"<a href='/login-pwd?mode={mode}'>Login with password</a>" : "")),
        "/login-pwd" => Ok(MobilePage(mode, "/after-id", "")),
        "/after-id-otp" => Ok(SmsPage(mode, withSwitch: false)),
        "/after-id" => Ok(Page($"<h3>Enter your password</h3><form action='/after-pwd'>{Mode(mode)}<input type='password' name='pwd'><button type='submit'>Login</button></form>")),
        "/after-pwd" when q["pwd"] != "pw" => Ok(Page($"<p>Incorrect password. Try again.</p><form action='/after-pwd'>{Mode(mode)}<input type='password' name='pwd'><button type='submit'>Login</button></form>")),
        "/after-pwd" when trusted => Ok(TotpPage(mode)),
        "/after-pwd" => mode switch
        {
            "sms" or "session" => Ok(SmsPage(mode, withSwitch: false)),
            "smsswitch" => Ok(SmsPage(mode, withSwitch: true)),
            "split" => Ok(SplitTotpPage(mode)),
            "captcha" => Ok(Page("<h3>Security check</h3><p>Please solve the captcha to continue</p><canvas></canvas>")),
            _ => Ok(TotpPage(mode)),
        },
        "/totp" => Ok(TotpPage(mode)),
        "/verify-sms" when q["otp"] == SmsCode => SmsAccepted(mode, q["trust"] == "on"),
        "/verify-sms" => Rejected(q["otp"], SmsForm(mode, "<p>Invalid OTP. Please try again.</p>")),
        "/verify" => Verify(mode, q["code"] ?? string.Concat(Enumerable.Range(1, 6).Select(i => q[$"d{i}"]))),
        "/verify-pin" when q["pin"] == "1234" => Accepted(q["pin"]!),
        "/verify-pin" => Rejected(q["pin"], PinPage(mode, "<p>Incorrect PIN</p>")),
        "/callback" => Ok(Page("<p>Logged in</p>")),
        _ => new(404, Page("not found")),
    };

    private Reply Verify(string mode, string code)
    {
        Codes.Add(code);
        var now = DateTime.UtcNow;
        var valid = mode != "badtotp" && new[] { now, now.AddSeconds(-30) }.Select(t => Totp.Compute(Secret, t)).Contains(code);
        if (!valid) return Ok(Page($"<p>Invalid code</p><form action='/verify'>{Mode(mode)}<input name='code' inputmode='numeric' maxlength='6'><button type='submit'>Verify</button></form>"));
        return mode == "pin" ? Ok(PinPage(mode, "")) : Done("session=1; Path=/");
    }

    private Reply SmsAccepted(string mode, bool trust)
    {
        Codes.Add(SmsCode);
        return Done(trust ? "device=trusted; Path=/; Max-Age=86400" : null);
    }

    private Reply Accepted(string code)
    {
        Codes.Add(code);
        return Done();
    }

    private Reply Rejected(string? code, string html)
    {
        Codes.Add(code ?? "");
        return Ok(html);
    }

    private static Reply Done(string? cookie = null) => new(302, "", "/callback?requestToken=tok-123&state=tb7", cookie);

    private string SmsPage(string mode, bool withSwitch)
    {
        Interlocked.Increment(ref _otpsSent);
        return SmsForm(mode, "", withSwitch);
    }

    private static string SmsForm(string mode, string error, bool withSwitch = false)
    {
        var sw = withSwitch ? $"<a href='/totp?mode={mode}'>Use authenticator app instead</a>" : "";
        return Page($"{error}<p>Enter the OTP sent to your mobile ******3210</p><form action='/verify-sms'>{Mode(mode)}<input name='otp' inputmode='numeric' maxlength='6'>" +
            $"<label><input type='checkbox' name='trust'> Trust this device</label><button type='submit'>Verify</button></form><p>Resend OTP in 29s</p>{sw}");
    }

    private static string PinPage(string mode, string error) => Page(
        $"{error}<h3>Enter your 4-digit PIN</h3><form action='/verify-pin'>{Mode(mode)}<input type='password' name='pin' maxlength='4' inputmode='numeric'><button type='submit'>Continue</button></form>");

    // The link mentions an SMS OTP: the prompt is still the authenticator, so the TOTP must go in
    private static string TotpPage(string mode) => Page(
        $"<h3>Enter the 6-digit code from your authenticator app</h3><form action='/verify'>{Mode(mode)}<input name='code' inputmode='numeric' maxlength='6' autocomplete='one-time-code'><button type='submit'>Verify</button></form><a href='#'>Use OTP sent to mobile instead</a>");

    private static string SplitTotpPage(string mode) => Page(
        $"<h3>Enter the code from your authenticator app</h3><form action='/verify'>{Mode(mode)}" +
        string.Concat(Enumerable.Range(1, 6).Select(i => $"<input name='d{i}' maxlength='1' size='1'>")) +
        "<button type='submit'>Verify</button></form>" +
        "<script>const b=[...document.querySelectorAll('input[maxlength=\"1\"]')];b.forEach((e,i)=>e.addEventListener('input',()=>{if(e.value&&b[i+1])b[i+1].focus();}));</script>");

    private static string MobilePage(string mode, string action, string extra) =>
        Page($"<h3>Login to Paytm Money</h3><form action='{action}'>{Mode(mode)}<input type='tel' name='mobile'><button type='submit'>Proceed</button></form>{extra}");

    private static string Mode(string mode) => $"<input type='hidden' name='mode' value='{mode}'>";
    private static string Page(string body) => $"<!doctype html><html><body>{body}</body></html>";
    private static Reply Ok(string html) => new(200, html);

    public void Dispose() => _http.Stop();
}

/// <summary>Stands in for the dashboard user: answers each OTP prompt from a queue (null = nobody typed one).</summary>
internal sealed class ScriptedOtp(params string?[] answers) : ISmsOtpPrompt
{
    private readonly Queue<string?> _answers = new(answers);
    public List<(string? SentTo, string? Note)> Asked { get; } = [];

    public Task<string?> AskAsync(TradingAccount account, string? sentTo, string? note, TimeSpan timeout, CancellationToken ct)
    {
        Asked.Add((sentTo, note));
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}

public sealed class BrokerLoginFlowTests : IDisposable
{
    private readonly FakeLoginSite _site = new();
    private readonly string _profiles = Path.Combine(Path.GetTempPath(), "tradebot-login-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _site.Dispose();
        try { Directory.Delete(_profiles, true); } catch (IOException) { }
    }

    /// <summary>The automation's own Chromium if installed, else the one preinstalled for the e2e tests.</summary>
    private static string? Chromium() =>
        Environment.GetEnvironmentVariable("TRADEBOT_TEST_CHROMIUM") is { Length: > 0 } p ? p
        : new[] { "/opt/pw-browsers", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/ms-playwright") }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetDirectories(d, "chromium-*"))
            .Select(d => Path.Combine(d, "chrome-linux64", "chrome"))
            .FirstOrDefault(File.Exists);

    private PlaywrightBrokerLogin Login(ISmsOtpPrompt? otp = null) => new(Options.Create(new BrokerLoginOptions
    {
        ChromiumExecutablePath = Chromium(), StepTimeoutSeconds = 4, OptionalStepTimeoutSeconds = 2, TimeoutSeconds = 6,
        ScreenshotDirectory = Path.Combine(Path.GetTempPath(), "tradebot-login-test"), ProfileDirectory = _profiles,
    }), otp ?? new ScriptedOtp(), TimeProvider.System, NullLogger<PlaywrightBrokerLogin>.Instance);

    private static TradingAccount Account(string password = "pw") => new()
    {
        AccountId = 7, AccountName = "Real", ClientId = "C7", UserName = "u", LoginId = "9876543210", LoginPassword = password,
        TotpSecret = FakeLoginSite.Secret, LoginPin = "1234",
    };

    private Task<string> Run(string mode, string password = "pw", ISmsOtpPrompt? otp = null) =>
        Login(otp).GetRequestTokenAsync(Account(password), $"{_site.Root}/login?mode={mode}", CancellationToken.None);

    [Theory]
    [InlineData("totp")]        // authenticator prompt next to a "Use OTP sent to mobile" link
    [InlineData("split")]       // six one-digit boxes
    [InlineData("otplogin")]    // OTP login by default, password login offered: take it before an OTP is sent
    [InlineData("smsswitch")]   // SMS OTP screen with a switch to the authenticator (one OTP is unavoidable)
    public async Task Logs_in_with_the_totp_and_returns_the_request_token(string mode)
    {
        if (Chromium() is null) return;
        Assert.Equal("tok-123", await Run(mode));
        Assert.Equal(Totp.Compute(FakeLoginSite.Secret, DateTime.UtcNow).Length, Assert.Single(_site.Codes).Length);
        Assert.Equal(mode == "smsswitch" ? 1 : 0, _site.OtpsSent);
    }

    [Fact]
    public async Task The_sms_otp_typed_on_the_dashboard_logs_in_and_the_device_then_uses_totp()
    {
        if (Chromium() is null) return;
        var otp = new ScriptedOtp(FakeLoginSite.SmsCode);
        Assert.Equal("tok-123", await Run("sms", otp: otp));
        Assert.Equal("******3210", Assert.Single(otp.Asked).SentTo);

        // Same saved profile next day: Paytm knows the device and asks for the TOTP, no SMS
        Assert.Equal("tok-123", await Run("sms", otp: otp));
        Assert.Single(otp.Asked);
        Assert.Equal(1, _site.OtpsSent);
        Assert.Equal(Totp.Compute(FakeLoginSite.Secret, DateTime.UtcNow).Length, _site.Codes[^1].Length);
    }

    [Fact]
    public async Task An_otp_only_flow_is_answered_from_the_dashboard()
    {
        if (Chromium() is null) return;
        Assert.Equal("tok-123", await Run("otponly", otp: new ScriptedOtp(FakeLoginSite.SmsCode)));
        Assert.Equal(1, _site.OtpsSent);
    }

    [Fact]
    public async Task A_rejected_sms_otp_is_asked_for_again()
    {
        if (Chromium() is null) return;
        var otp = new ScriptedOtp("000000", FakeLoginSite.SmsCode);
        Assert.Equal("tok-123", await Run("sms", otp: otp));
        Assert.Equal(2, otp.Asked.Count);
        Assert.Contains("rejected", otp.Asked[1].Note);
        Assert.Equal(1, _site.OtpsSent);
    }

    [Theory]
    [InlineData("sms")]         // only an SMS OTP after the password
    [InlineData("otponly")]     // SMS OTP straight after the mobile number, no password option
    public async Task Without_a_dashboard_otp_the_login_stops_without_submitting_anything(string mode)
    {
        if (Chromium() is null) return;
        var e = await Assert.ThrowsAsync<BrokerLoginException>(() => Run(mode));
        Assert.Contains("SMS OTP", e.Message);
        Assert.Equal(1, _site.OtpsSent);
        Assert.Empty(_site.Codes);
    }

    [Fact]
    public async Task A_pin_screen_after_the_totp_gets_the_login_pin()
    {
        if (Chromium() is null) return;
        Assert.Equal("tok-123", await Run("pin"));
        Assert.Equal(["1234"], _site.Codes.Skip(1));
    }

    [Fact]
    public async Task A_remembered_session_redirects_straight_away()
    {
        if (Chromium() is null) return;
        Assert.Equal("tok-123", await Run("session", otp: new ScriptedOtp(FakeLoginSite.SmsCode)));
        var codes = _site.Codes.Count;
        Assert.Equal("tok-123", await Run("session"));
        Assert.Equal(codes, _site.Codes.Count);
    }

    [Fact]
    public async Task A_rejected_totp_is_reported_and_not_resubmitted()
    {
        if (Chromium() is null) return;
        var e = await Assert.ThrowsAsync<BrokerLoginException>(() => Run("badtotp"));
        Assert.Contains("TOTP secret", e.Message);
        Assert.Single(_site.Codes);
    }

    [Fact]
    public async Task An_unrecognised_page_after_the_password_is_described_and_not_retried()
    {
        if (Chromium() is null) return;
        var e = await Assert.ThrowsAsync<BrokerLoginException>(() => Run("captcha"));
        Assert.Contains("solve the captcha", e.Message);
        Assert.Equal(0, _site.OtpsSent);
    }

    [Fact]
    public async Task A_rejected_password_is_reported_and_not_retried()
    {
        if (Chromium() is null) return;
        var e = await Assert.ThrowsAsync<BrokerLoginException>(() => Run("totp", password: "wrong"));
        Assert.Contains("password", e.Message);
        Assert.Empty(_site.Codes);
    }

    [Theory]
    [InlineData("Enter the 6-digit code from your Authenticator app", LoginScreen.Authenticator)]
    [InlineData("Enter TOTP", LoginScreen.Authenticator)]
    [InlineData("We have sent an OTP to +91 ******3210", LoginScreen.SmsOtp)]
    [InlineData("Enter the OTP sent to your mobile number", LoginScreen.SmsOtp)]
    [InlineData("Verify OTP Resend in 30s", LoginScreen.SmsOtp)]
    [InlineData("Enter your 4-digit passcode", LoginScreen.Pin)]
    [InlineData("Incorrect password, 2 attempts left", LoginScreen.BadPassword)]
    [InlineData("Your account is locked", LoginScreen.BadPassword)]
    [InlineData("Login to Paytm Money", LoginScreen.Unknown)]
    public void Screens_are_recognised_from_their_prompt(string prompt, LoginScreen expected) =>
        Assert.Equal(expected, Login().Classify(prompt));

    [Theory]
    [InlineData("Enter the OTP sent to +91 ******3210", "+91 ******3210")]
    [InlineData("OTP sent to xxxxxx7788 via SMS", "xxxxxx7788")]
    [InlineData("Enter the OTP", null)]
    public void The_masked_number_is_read_from_the_prompt(string prompt, string? expected) =>
        Assert.Equal(expected, PlaywrightBrokerLogin.SentTo(prompt));
}
