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
/// "sends" so tests can prove the automation never makes Paytm text the user again.
/// </summary>
internal sealed class FakeLoginSite : IDisposable
{
    public const string Secret = "JBSWY3DPEHPK3PXP";
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

    private async Task LoopAsync()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            var q = ctx.Request.QueryString;
            var mode = q["mode"] ?? "totp";
            var (status, html, location) = Route(ctx.Request.Url!.AbsolutePath, mode, q);
            ctx.Response.StatusCode = status;
            if (location is not null) ctx.Response.RedirectLocation = location;
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private (int, string, string?) Route(string path, string mode, System.Collections.Specialized.NameValueCollection q) => path switch
    {
        "/login" => Ok(MobilePage(mode, mode is "otplogin" or "otponly" ? "/after-id-otp" : "/after-id",
            mode == "otplogin" ? $"<a href='/login-pwd?mode={mode}'>Login with password</a>" : "")),
        "/login-pwd" => Ok(MobilePage(mode, "/after-id", "")),
        "/after-id-otp" => Ok(SmsPage(mode, sent: true, withSwitch: false)),
        "/after-id" => Ok(Page($"<h3>Enter your password</h3><form action='/after-pwd'>{Mode(mode)}<input type='password' name='pwd'><button type='submit'>Login</button></form>")),
        "/after-pwd" when q["pwd"] != "pw" => Ok(Page($"<p>Incorrect password. Try again.</p><form action='/after-pwd'>{Mode(mode)}<input type='password' name='pwd'><button type='submit'>Login</button></form>")),
        "/after-pwd" => mode switch
        {
            "sms" => Ok(SmsPage(mode, sent: true, withSwitch: false)),
            "smsswitch" => Ok(SmsPage(mode, sent: true, withSwitch: true)),
            "split" => Ok(SplitTotpPage(mode)),
            _ => Ok(TotpPage(mode)),
        },
        "/totp" => Ok(TotpPage(mode)),
        "/verify" => Verify(q["code"] ?? string.Concat(Enumerable.Range(1, 6).Select(i => q[$"d{i}"]))),
        "/callback" => Ok(Page("<p>Logged in</p>")),
        _ => (404, Page("not found"), null),
    };

    private (int, string, string?) Verify(string code)
    {
        Codes.Add(code);
        var now = DateTime.UtcNow;
        var valid = new[] { now, now.AddSeconds(-30) }.Select(t => Totp.Compute(Secret, t)).Contains(code);
        return valid ? (302, "", "/callback?requestToken=tok-123&state=tb7") : (200, Page("<p>Invalid code</p>"), null);
    }

    private string SmsPage(string mode, bool sent, bool withSwitch)
    {
        if (sent) Interlocked.Increment(ref _otpsSent);
        var sw = withSwitch ? $"<a href='/totp?mode={mode}'>Use authenticator app instead</a>" : "";
        return Page($"<p>Enter the OTP sent to your mobile ******3210</p><form action='/verify'>{Mode(mode)}<input name='otp' inputmode='numeric' maxlength='6'><button type='submit'>Verify</button></form>{sw}");
    }

    // The link mentions an SMS OTP: the prompt is still the authenticator, so the TOTP must go in
    private static string TotpPage(string mode) => Page(
        $"<h3>Enter the 6-digit code from your authenticator app</h3><form action='/verify'>{Mode(mode)}<input name='code' inputmode='numeric' maxlength='6' autocomplete='one-time-code'><button type='submit'>Verify</button></form><a href='#'>Use OTP sent to mobile instead</a>");

    private static string SplitTotpPage(string mode) => Page(
        $"<h3>Enter the code from your authenticator app</h3><form action='/verify'>{Mode(mode)}" +
        string.Concat(Enumerable.Range(1, 6).Select(i => $"<input name='d{i}' maxlength='1' inputmode='numeric' size='1'>")) +
        "<button type='submit'>Verify</button></form>" +
        "<script>const b=[...document.querySelectorAll('input[maxlength=\"1\"]')];b.forEach((e,i)=>e.addEventListener('input',()=>{if(e.value&&b[i+1])b[i+1].focus();}));</script>");

    private static string MobilePage(string mode, string action, string extra) =>
        Page($"<h3>Login to Paytm Money</h3><form action='{action}'>{Mode(mode)}<input type='tel' name='mobile'><button type='submit'>Proceed</button></form>{extra}");

    private static string Mode(string mode) => $"<input type='hidden' name='mode' value='{mode}'>";
    private static string Page(string body) => $"<!doctype html><html><body>{body}</body></html>";
    private static (int, string, string?) Ok(string html) => (200, html, null);

    public void Dispose() => _http.Stop();
}

public sealed class BrokerLoginFlowTests : IDisposable
{
    private readonly FakeLoginSite _site = new();

    public void Dispose() => _site.Dispose();

    /// <summary>The automation's own Chromium if installed, else the one preinstalled for the e2e tests.</summary>
    private static string? Chromium() =>
        Environment.GetEnvironmentVariable("TRADEBOT_TEST_CHROMIUM") is { Length: > 0 } p ? p
        : new[] { "/opt/pw-browsers", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/ms-playwright") }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetDirectories(d, "chromium-*"))
            .Select(d => Path.Combine(d, "chrome-linux64", "chrome"))
            .FirstOrDefault(File.Exists);

    private PlaywrightBrokerLogin Login() => new(Options.Create(new BrokerLoginOptions
    {
        ChromiumExecutablePath = Chromium(), StepTimeoutSeconds = 4, OptionalStepTimeoutSeconds = 2, TimeoutSeconds = 10,
        ScreenshotDirectory = Path.Combine(Path.GetTempPath(), "tradebot-login-test"),
    }), TimeProvider.System, NullLogger<PlaywrightBrokerLogin>.Instance);

    private static TradingAccount Account(string password = "pw") => new()
    {
        AccountId = 7, AccountName = "Real", ClientId = "C7", UserName = "u", LoginId = "9876543210", LoginPassword = password, TotpSecret = FakeLoginSite.Secret,
    };

    private Task<string> Run(string mode, string password = "pw") =>
        Login().GetRequestTokenAsync(Account(password), $"{_site.Root}/login?mode={mode}", CancellationToken.None);

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

    [Theory]
    [InlineData("sms")]         // only an SMS OTP after the password
    [InlineData("otponly")]     // SMS OTP straight after the mobile number, no password option
    public async Task An_sms_otp_screen_stops_the_login_without_submitting_anything(string mode)
    {
        if (Chromium() is null) return;
        var e = await Assert.ThrowsAsync<BrokerLoginException>(() => Run(mode));
        Assert.Contains("SMS OTP", e.Message);
        Assert.Equal(1, _site.OtpsSent);
        Assert.Empty(_site.Codes);
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
    [InlineData("Incorrect password, 2 attempts left", LoginScreen.BadPassword)]
    [InlineData("Your account is locked", LoginScreen.BadPassword)]
    [InlineData("Login to Paytm Money", LoginScreen.Unknown)]
    public void Screens_are_recognised_from_their_prompt(string prompt, LoginScreen expected) =>
        Assert.Equal(expected, Login().Classify(prompt));
}
