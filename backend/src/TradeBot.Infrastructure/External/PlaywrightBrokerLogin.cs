using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Infrastructure.External;

public sealed record LoginStep(string Action, string Selector, string? Value = null, bool Optional = false);

/// <summary>What the login page is asking for, judged from its prompt text (links and buttons excluded).</summary>
public enum LoginScreen { Unknown, Authenticator, SmsOtp, Pin, BadPassword, RejectedCode }

/// <summary>
/// Bound from "BrokerLogin". The step list and screen patterns are data, so the flow can follow login-page changes
/// without a rebuild. Placeholders: {loginId} {password} {totp} {pin}.
/// Actions: fill, click, press, waitfor, and secondfactor (answers each code screen in turn until the redirect:
/// the TOTP on an authenticator screen, the PIN on a PIN screen, and on an SMS-OTP screen the switch to the
/// authenticator if offered, otherwise the OTP typed on the dashboard).
/// </summary>
public sealed class BrokerLoginOptions
{
    public string? ChromiumExecutablePath { get; set; }
    public bool Headless { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 90;
    public int StepTimeoutSeconds { get; set; } = 15;
    /// <summary>Optional steps give up quickly: they are probes ("is there a Login with password link?").</summary>
    public int OptionalStepTimeoutSeconds { get; set; } = 4;
    public string ScreenshotDirectory { get; set; } = "/var/lib/tradebot/auth-failures";
    /// <summary>
    /// One saved Chromium profile per account, so Paytm sees a known device (cookies, "trust this device") rather
    /// than a brand-new browser that it challenges with an SMS OTP every day. Empty = a throwaway profile each time.
    /// </summary>
    public string? ProfileDirectory { get; set; } = "/var/lib/tradebot/browser-profiles";
    /// <summary>How long the dashboard OTP box stays open when Paytm sends an SMS OTP.</summary>
    public int SmsOtpWaitSeconds { get; set; } = 180;

    public string AuthenticatorPattern { get; set; } = @"(?i)authenticator|\btotp\b|authentication app|time[- ]based";
    public string SmsOtpPattern { get; set; } = @"(?i)\botp\b|one[- ]time password|verification code";
    public string PinPattern { get; set; } = @"(?i)\b(pin|mpin|passcode)\b";
    public string BadPasswordPattern { get; set; } = @"(?i)\b(incorrect|invalid|wrong)\b.{0,20}\b(password|credentials)\b|\baccount\b.{0,20}\b(locked|blocked)\b";
    public string RejectedCodePattern { get; set; } = @"(?i)\b(incorrect|invalid|wrong|expired)\b.{0,30}\b(otp|totp|code|pin|passcode)\b|\b(otp|totp|code|pin|passcode)\b.{0,30}\b(incorrect|invalid|wrong|expired|mismatch)\b";
    /// <summary>Link or button on an SMS-OTP screen that switches to the authenticator code.</summary>
    public string SwitchToAuthenticatorSelector { get; set; } = "a:text-matches('authenticator|totp', 'i'), button:text-matches('authenticator|totp', 'i'), [role=button]:text-matches('authenticator|totp', 'i')";
    public string CodeInputSelector { get; set; } =
        "input[autocomplete='one-time-code'], input[name*='totp' i], input[name*='otp' i], input[name*='code' i], input[name*='pin' i], " +
        "input[placeholder*='otp' i], input[placeholder*='code' i], input[placeholder*='pin' i], input[aria-label*='otp' i], input[aria-label*='code' i], " +
        "input[inputmode='numeric'], input[type='number'], input[maxlength='1'], input[maxlength='4'], input[maxlength='6']";
    public string SubmitSelector { get; set; } = "button[type='submit'], button:text-matches('verify|proceed|continue|submit|login|sign in', 'i'), [role=button]:text-matches('verify|proceed|continue|submit', 'i')";
    /// <summary>"Remember / trust this device" checkbox, ticked before a code is submitted.</summary>
    public string TrustDevicePattern { get; set; } = @"(?i)remember|trust|don.?t ask";

    public List<LoginStep> Steps { get; set; } =
    [
        // Prefer the password flow when the page offers it; an OTP-login flow would make Paytm send an SMS
        new("click", "a:text-matches('(login|sign in) (with|using) password', 'i'), button:text-matches('(login|sign in) (with|using) password', 'i')", Optional: true),
        new("fill", "input[type='tel'], input[type='email'], input[name*='mobile' i], input[name*='email' i], input[name*='user' i]", "{loginId}"),
        new("click", "button[type='submit'], button:has-text('Proceed'), button:has-text('Next'), button:has-text('Continue')", Optional: true),
        new("fill", "input[type='password']", "{password}"),
        new("click", "button[type='submit'], button:has-text('Login'), button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
        new("secondfactor", ""),
        // Consent page some broker logins show before redirecting to the app
        new("click", "button:text-matches('^\\s*(allow|authori[sz]e|accept|proceed|continue)', 'i')", Optional: true),
    ];
}

/// <summary>Headless Chromium login that captures the requestToken from the broker's redirect.</summary>
public sealed partial class PlaywrightBrokerLogin(
    IOptions<BrokerLoginOptions> options, ISmsOtpPrompt otpPrompt, TimeProvider clock, ILogger<PlaywrightBrokerLogin> log) : IBrokerLoginAutomation
{
    private readonly BrokerLoginOptions _o = options.Value;
    private static string? _userAgent;

    public const string SmsOtpMessage =
        "Paytm asked for the SMS OTP sent to your mobile and none was entered on the dashboard in time. " +
        "Start the login again and type the OTP in the account card; Paytm should then remember this device and use TOTP.";

    public async Task<string> GetRequestTokenAsync(TradingAccount account, string loginUrl, CancellationToken ct)
    {
        using var pw = await Playwright.CreateAsync();
        await using var context = await OpenContextAsync(pw, account);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        page.SetDefaultTimeout(_o.StepTimeoutSeconds * 1000);

        var token = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(string url) { if (ExtractRequestToken(url) is { } t) token.TrySetResult(t); }
        page.Request += (_, r) => Check(r.Url);
        page.FrameNavigated += (_, f) => Check(f.Url);
        var flow = new Flow(page, account, token.Task);

        try
        {
            await page.GotoAsync(loginUrl);
            foreach (var step in _o.Steps)
            {
                if (token.Task.IsCompleted) break;
                try
                {
                    await RunStepAsync(flow, step, ct);
                    if (step.Value?.Contains("{loginId}") == true) flow.LoginIdSent = true;
                }
                catch (Exception e) when (e is PlaywrightException or TimeoutException)
                {
                    // A remembered session can redirect straight away
                    if (token.Task.IsCompleted) break;
                    if (step.Optional) { log.LogDebug("Optional login step {Action} {Selector} skipped", step.Action, step.Selector); continue; }
                    // The page skipped ahead (OTP or TOTP login without a password): answer it from here
                    if (await OnCodeScreenAsync(page)) { await SecondFactorAsync(flow, ct); break; }
                    await ThrowIfDeadEndAsync(flow, $"waiting for {step.Selector}");
                    throw;
                }
            }
            var done = await Task.WhenAny(token.Task, Task.Delay(TimeSpan.FromSeconds(_o.TimeoutSeconds), ct));
            if (done != token.Task)
            {
                await ThrowIfDeadEndAsync(flow, "waiting for the redirect");
                throw new TimeoutException("Login did not redirect with a requestToken.");
            }
            return await token.Task;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await SaveScreenshotAsync(page, account);
            throw;
        }
    }

    private sealed class Flow(IPage page, TradingAccount account, Task<string> token)
    {
        public IPage Page { get; } = page;
        public TradingAccount Account { get; } = account;
        public Task<string> Token { get; } = token;
        /// <summary>From here on a new attempt can make Paytm text another OTP, so unknown pages are not retried.</summary>
        public bool LoginIdSent { get; set; }
    }

    private async Task<IBrowserContext> OpenContextAsync(IPlaywright pw, TradingAccount account)
    {
        var args = new[] { "--disable-blink-features=AutomationControlled" };
        _userAgent ??= await DesktopUserAgentAsync(pw, args);
        if (string.IsNullOrWhiteSpace(_o.ProfileDirectory))
        {
            var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _o.Headless, ExecutablePath = _o.ChromiumExecutablePath, Args = args });
            var ctx = await browser.NewContextAsync(new BrowserNewContextOptions { UserAgent = _userAgent, Locale = "en-IN", TimezoneId = "Asia/Kolkata" });
            ctx.Close += async (_, _) => await browser.CloseAsync();
            return ctx;
        }
        var dir = Path.Combine(_o.ProfileDirectory, $"account-{account.AccountId}");
        Directory.CreateDirectory(dir);
        return await pw.Chromium.LaunchPersistentContextAsync(dir, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _o.Headless, ExecutablePath = _o.ChromiumExecutablePath, Args = args,
            UserAgent = _userAgent, Locale = "en-IN", TimezoneId = "Asia/Kolkata", ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
    }

    /// <summary>Headless Chromium announces itself as "HeadlessChrome"; present the same version as desktop Chrome.</summary>
    private async Task<string> DesktopUserAgentAsync(IPlaywright pw, string[] args)
    {
        await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = _o.ChromiumExecutablePath, Args = args });
        var major = browser.Version.Split('.')[0];
        return $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
    }

    private async Task RunStepAsync(Flow f, LoginStep step, CancellationToken ct)
    {
        var locator = f.Page.Locator(step.Selector).First;
        var timeout = (step.Optional ? _o.OptionalStepTimeoutSeconds : _o.StepTimeoutSeconds) * 1000f;
        switch (step.Action.ToLowerInvariant())
        {
            case "fill":
                var value = await ResolveAsync(step.Value ?? "", f.Account, ct);
                if (string.IsNullOrEmpty(value)) { if (step.Optional) return; throw new BrokerLoginException($"No value for login step {step.Selector}."); }
                await locator.FillAsync(value, new LocatorFillOptions { Timeout = timeout });
                break;
            case "click":
                await locator.ClickAsync(new LocatorClickOptions { Timeout = timeout });
                break;
            case "press":
                await locator.PressAsync(step.Value ?? "Enter", new LocatorPressOptions { Timeout = timeout });
                break;
            case "waitfor":
                await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = timeout });
                break;
            case "secondfactor":
                await SecondFactorAsync(f, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown login step action '{step.Action}'.");
        }
    }

    private ILocator CodeInputs(IPage page) => page.Locator(_o.CodeInputSelector).Filter(new LocatorFilterOptions { Visible = true });

    private async Task<bool> OnCodeScreenAsync(IPage page)
    {
        try { return await CodeInputs(page).CountAsync() > 0 && Classify(await PromptTextAsync(page)) is not (LoginScreen.Unknown or LoginScreen.BadPassword); }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// Answers code screens until the redirect: TOTP (also on an unlabelled code screen), PIN, or the SMS OTP (switching
    /// to the authenticator when the page offers it, else asking the dashboard). Never re-submits a rejected TOTP or PIN.
    /// </summary>
    private async Task SecondFactorAsync(Flow f, CancellationToken ct)
    {
        var page = f.Page;
        var smsDeadline = (DateTime?)null;
        string? smsNote = null;
        for (var round = 0; round < 8 && !f.Token.IsCompleted; round++)
        {
            // No (more) code screens: the redirect, or a consent page the following steps handle
            if (!await WaitForCodeInputAsync(f))
            {
                if (!f.Token.IsCompleted && Classify(await PromptTextAsync(page)) == LoginScreen.BadPassword) throw new BrokerLoginException(BadPasswordMessage);
                return;
            }
            var prompt = await PromptTextAsync(page);
            var screen = Classify(prompt);
            string code;
            switch (screen)
            {
                case LoginScreen.BadPassword:
                    throw new BrokerLoginException(BadPasswordMessage);
                case LoginScreen.SmsOtp:
                    var toAuthenticator = page.Locator(_o.SwitchToAuthenticatorSelector).Filter(new LocatorFilterOptions { Visible = true }).First;
                    if (await toAuthenticator.CountAsync() > 0)
                    {
                        log.LogInformation("Login page offered SMS OTP; switching to the authenticator code");
                        await toAuthenticator.ClickAsync();
                        await page.WaitForLoadStateAsync();
                        await page.WaitForTimeoutAsync(500);
                        if (Classify(await PromptTextAsync(page)) != LoginScreen.SmsOtp) continue;
                    }
                    smsDeadline ??= clock.GetUtcNow().UtcDateTime.AddSeconds(_o.SmsOtpWaitSeconds);
                    var left = smsDeadline.Value - clock.GetUtcNow().UtcDateTime;
                    if (left < TimeSpan.FromSeconds(10)) throw new BrokerLoginException(SmsOtpMessage);
                    code = await otpPrompt.AskAsync(f.Account, SentTo(prompt), smsNote, left, ct) ?? throw new BrokerLoginException(SmsOtpMessage);
                    break;
                case LoginScreen.Pin:
                    code = f.Account.LoginPin is { Length: > 0 } pin ? pin
                        : throw new BrokerLoginException("Paytm asks for the login PIN. Save it as the account's Login PIN in the profile, then retry.");
                    break;
                default:
                    code = await ResolveAsync("{totp}", f.Account, ct);
                    break;
            }

            await EnterCodeAsync(page, code);
            switch (await AfterSubmitAsync(f))
            {
                case LoginScreen.RejectedCode when screen == LoginScreen.SmsOtp:
                    smsNote = "Paytm rejected the last OTP; enter the latest one from the SMS.";
                    await ClearCodeInputsAsync(page);
                    continue;
                case LoginScreen.RejectedCode when screen == LoginScreen.Pin:
                    throw new BrokerLoginException("Paytm rejected the login PIN. Fix it in the account profile; not retried, to avoid locking the account.");
                case LoginScreen.RejectedCode:
                    throw new BrokerLoginException("Paytm rejected the TOTP code. Check that the account's TOTP secret is the one enabled in Paytm Money and that the server clock is right (timedatectl).");
                case LoginScreen.BadPassword:
                    throw new BrokerLoginException(BadPasswordMessage);
            }
        }
    }

    private const string BadPasswordMessage = "Paytm rejected the login ID or password. Fix them in the account profile; not retried, to avoid locking the account.";

    private async Task<bool> WaitForCodeInputAsync(Flow f)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_o.StepTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (f.Token.IsCompleted) return false;
            try { if (await CodeInputs(f.Page).CountAsync() > 0) return true; } catch (PlaywrightException) { /* navigating */ }
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>Six one-digit boxes get typed into (the page moves focus along); a single field is filled.</summary>
    private async Task EnterCodeAsync(IPage page, string code)
    {
        await TickTrustDeviceAsync(page);
        var inputs = CodeInputs(page);
        var boxes = inputs.And(page.Locator("input[maxlength='1']"));
        if (await boxes.CountAsync() >= 4)
        {
            await boxes.First.ClickAsync();
            await page.Keyboard.TypeAsync(code, new KeyboardTypeOptions { Delay = 60 });
        }
        else await inputs.First.FillAsync(code);

        // Many code screens submit themselves after the last digit
        await page.WaitForTimeoutAsync(1200);
        try
        {
            if (await inputs.CountAsync() == 0) return;
            var submit = page.Locator(_o.SubmitSelector).Filter(new LocatorFilterOptions { Visible = true }).First;
            if (await submit.CountAsync() > 0 && await submit.IsEnabledAsync()) await submit.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            else await inputs.Last.PressAsync("Enter", new LocatorPressOptions { Timeout = 3000 });
        }
        catch (PlaywrightException) { /* the page moved on by itself */ }
    }

    private async Task TickTrustDeviceAsync(IPage page)
    {
        try
        {
            var box = page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions { NameRegex = new Regex(_o.TrustDevicePattern) }).Filter(new LocatorFilterOptions { Visible = true }).First;
            if (await box.CountAsync() > 0 && !await box.IsCheckedAsync()) await box.CheckAsync(new LocatorCheckOptions { Timeout = 2000 });
        }
        catch (PlaywrightException e) { log.LogDebug(e, "Could not tick the trust-this-device box"); }
    }

    private async Task ClearCodeInputsAsync(IPage page)
    {
        try { foreach (var input in await CodeInputs(page).AllAsync()) await input.FillAsync(""); }
        catch (PlaywrightException) { /* replaced by a fresh screen */ }
    }

    /// <summary>After a code is submitted: the redirect, a new screen (Unknown), or the page's rejection.</summary>
    private async Task<LoginScreen> AfterSubmitAsync(Flow f)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_o.StepTimeoutSeconds);
        while (DateTime.UtcNow < deadline && !f.Token.IsCompleted)
        {
            try
            {
                var text = await PromptTextAsync(f.Page);
                if (Regex.IsMatch(text, _o.BadPasswordPattern)) return LoginScreen.BadPassword;
                if (Regex.IsMatch(text, _o.RejectedCodePattern)) return LoginScreen.RejectedCode;
                var filled = await CodeInputs(f.Page).EvaluateAllAsync<bool>("els => els.length > 0 && els.every(e => e.value)");
                if (!filled) return LoginScreen.Unknown;
            }
            catch (PlaywrightException) { /* navigating */ }
            await Task.Delay(300);
        }
        return LoginScreen.Unknown;
    }

    /// <summary>Turns a dead end into a non-retryable error once retrying could send another OTP or lock the account.</summary>
    private async Task ThrowIfDeadEndAsync(Flow f, string context)
    {
        string text;
        try { text = await PromptTextAsync(f.Page); } catch (PlaywrightException) { return; }
        if (Classify(text) == LoginScreen.BadPassword) throw new BrokerLoginException(BadPasswordMessage);
        if (f.LoginIdSent) throw new BrokerLoginException(Unrecognised(await DescribeAsync(f.Page), context));
    }

    private string Unrecognised(string page, string context) =>
        $"Paytm showed a page the bot does not recognise ({context}): {page}. Not retried, because each new attempt can make Paytm " +
        $"send another SMS OTP. A screenshot is saved in {_o.ScreenshotDirectory}; the BrokerLogin selectors and patterns can be adjusted to match it.";

    private static async Task<string> DescribeAsync(IPage page)
    {
        string text;
        try { text = await PromptTextAsync(page); } catch (PlaywrightException) { text = ""; }
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 240) text = text[..240] + "…";
        var url = page.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : page.Url;
        return $"{url} \"{text}\"";
    }

    /// <summary>The masked number the page says the OTP went to ("+91 ******3210"), if it shows one.</summary>
    public static string? SentTo(string prompt) =>
        Regex.Match(prompt, @"(\+?\d{0,3}[\s-]?)?[*xX•]{3,}[\s-]?\d{2,4}") is { Success: true } m ? m.Value.Trim() : null;

    public LoginScreen Classify(string prompt)
    {
        if (Regex.IsMatch(prompt, _o.BadPasswordPattern)) return LoginScreen.BadPassword;
        if (Regex.IsMatch(prompt, _o.AuthenticatorPattern)) return LoginScreen.Authenticator;
        if (Regex.IsMatch(prompt, _o.SmsOtpPattern)) return LoginScreen.SmsOtp;
        if (Regex.IsMatch(prompt, _o.PinPattern)) return LoginScreen.Pin;
        return LoginScreen.Unknown;
    }

    /// <summary>Visible page text outside links and buttons, so "Use OTP instead" never reads as the prompt.</summary>
    private static Task<string> PromptTextAsync(IPage page) => page.EvaluateAsync<string>("""
        () => {
          const skip = 'a, button, [role=button], [role=link], script, style, noscript';
          const out = [];
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          for (let n = walker.nextNode(); n; n = walker.nextNode()) {
            const el = n.parentElement;
            if (!el || el.closest(skip) || !el.offsetParent && getComputedStyle(el).position !== 'fixed') continue;
            const t = n.textContent.trim();
            if (t) out.push(t);
          }
          for (const i of document.querySelectorAll('input[placeholder]')) if (i.offsetParent) out.push(i.placeholder);
          return out.join(' ');
        }
        """);

    private async Task<string> ResolveAsync(string template, TradingAccount a, CancellationToken ct)
    {
        if (template.Contains("{totp}"))
        {
            // Avoid submitting a code that expires mid-request
            if (Totp.SecondsRemaining(clock.GetUtcNow().UtcDateTime) < 5) await Task.Delay(TimeSpan.FromSeconds(6), clock, ct);
            template = template.Replace("{totp}", a.TotpSecret is null ? "" : Totp.Compute(a.TotpSecret, clock.GetUtcNow().UtcDateTime));
        }
        return template.Replace("{loginId}", a.LoginId ?? a.Phone ?? a.Email ?? "")
            .Replace("{password}", a.LoginPassword ?? "")
            .Replace("{pin}", a.LoginPin ?? "");
    }

    private async Task SaveScreenshotAsync(IPage page, TradingAccount a)
    {
        try
        {
            Directory.CreateDirectory(_o.ScreenshotDirectory);
            var path = Path.Combine(_o.ScreenshotDirectory, $"login-{a.AccountId}-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            log.LogWarning("Login failure screenshot saved to {Path}", path);
        }
        catch (Exception e) { log.LogDebug(e, "Could not save login screenshot"); }
    }

    public static string? ExtractRequestToken(string url)
    {
        var m = RequestTokenRegex().Match(url);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"[?&](?:requestToken|request_token)=([^&#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RequestTokenRegex();
}
