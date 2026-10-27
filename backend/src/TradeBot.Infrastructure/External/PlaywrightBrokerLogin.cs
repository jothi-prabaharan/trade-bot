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
public enum LoginScreen { Unknown, Authenticator, SmsOtp, BadPassword }

/// <summary>
/// Bound from "BrokerLogin". The step list and screen patterns are data, so the flow can follow login-page changes
/// without a rebuild. Placeholders: {loginId} {password} {totp} {pin}.
/// Actions: fill, click, press, waitfor, and secondfactor (enters the TOTP only on an authenticator screen; on an
/// SMS-OTP screen it switches to the authenticator if the page offers that, otherwise it stops without retrying).
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

    public string AuthenticatorPattern { get; set; } = @"(?i)authenticator|\btotp\b|authentication app|time[- ]based";
    public string SmsOtpPattern { get; set; } = @"(?i)\botp\b.{0,60}\b(sent|sms|mobile|phone)\b|\b(sent|sms)\b.{0,60}\botp\b|verification code.{0,40}\b(sent|sms)\b";
    public string BadPasswordPattern { get; set; } = @"(?i)\b(incorrect|invalid|wrong)\b.{0,20}\b(password|credentials)\b|\baccount\b.{0,20}\b(locked|blocked)\b";
    /// <summary>Link or button on an SMS-OTP screen that switches to the authenticator code.</summary>
    public string SwitchToAuthenticatorSelector { get; set; } = "a:text-matches('authenticator|totp', 'i'), button:text-matches('authenticator|totp', 'i'), [role=button]:text-matches('authenticator|totp', 'i')";
    public string CodeInputSelector { get; set; } = "input[autocomplete='one-time-code'], input[name*='totp' i], input[name*='otp' i], input[name*='code' i], input[inputmode='numeric'], input[maxlength='6']";

    public List<LoginStep> Steps { get; set; } =
    [
        // Prefer the password flow when the page offers it; an OTP-login flow would make Paytm send an SMS
        new("click", "a:text-matches('(login|sign in) (with|using) password', 'i'), button:text-matches('(login|sign in) (with|using) password', 'i')", Optional: true),
        new("fill", "input[type='tel'], input[type='email'], input[name*='mobile' i], input[name*='email' i], input[name*='user' i]", "{loginId}"),
        new("click", "button[type='submit'], button:has-text('Proceed'), button:has-text('Next'), button:has-text('Continue')", Optional: true),
        new("fill", "input[type='password']", "{password}"),
        new("click", "button[type='submit'], button:has-text('Login'), button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
        new("secondfactor", "", "{totp}"),
        new("click", "button[type='submit'], button:has-text('Verify'), button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
        new("fill", "input[name*='pin' i], input[name*='passcode' i]", "{pin}", Optional: true),
        new("click", "button[type='submit'], button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
    ];
}

/// <summary>Headless Chromium login that captures the requestToken from the broker's redirect.</summary>
public sealed partial class PlaywrightBrokerLogin(IOptions<BrokerLoginOptions> options, TimeProvider clock, ILogger<PlaywrightBrokerLogin> log) : IBrokerLoginAutomation
{
    private readonly BrokerLoginOptions _o = options.Value;

    public const string SmsOtpMessage =
        "Paytm asked for the SMS OTP sent to your mobile, so this Paytm Money account is not logging in with TOTP. " +
        "Enable TOTP (authenticator app) in Paytm Money's security settings, save the same secret as this account's TOTP secret, " +
        "then retry. Automatic login stopped here so no more OTPs are sent.";

    public async Task<string> GetRequestTokenAsync(TradingAccount account, string loginUrl, CancellationToken ct)
    {
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _o.Headless, ExecutablePath = _o.ChromiumExecutablePath });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(_o.StepTimeoutSeconds * 1000);

        var token = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(string url) { if (ExtractRequestToken(url) is { } t) token.TrySetResult(t); }
        page.Request += (_, r) => Check(r.Url);
        page.FrameNavigated += (_, f) => Check(f.Url);

        try
        {
            await page.GotoAsync(loginUrl);
            foreach (var step in _o.Steps)
            {
                if (token.Task.IsCompleted) break;
                try { await RunStepAsync(page, step, account, ct); }
                catch (Exception e) when (e is PlaywrightException or TimeoutException)
                {
                    // A missing field often means the page went somewhere else: say where, and never retry into more OTPs
                    if (!token.Task.IsCompleted) await ThrowIfDeadEndAsync(page);
                    if (!step.Optional) throw;
                    log.LogDebug("Optional login step {Action} {Selector} skipped", step.Action, step.Selector);
                }
            }
            var done = await Task.WhenAny(token.Task, Task.Delay(TimeSpan.FromSeconds(_o.TimeoutSeconds), ct));
            if (done != token.Task)
            {
                await ThrowIfDeadEndAsync(page);
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

    private async Task RunStepAsync(IPage page, LoginStep step, TradingAccount a, CancellationToken ct)
    {
        var locator = page.Locator(step.Selector).First;
        var timeout = (step.Optional ? _o.OptionalStepTimeoutSeconds : _o.StepTimeoutSeconds) * 1000f;
        switch (step.Action.ToLowerInvariant())
        {
            case "fill":
                var value = await ResolveAsync(step.Value ?? "", a, ct);
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
                await SecondFactorAsync(page, a, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown login step action '{step.Action}'.");
        }
    }

    /// <summary>Enters the TOTP, but only where the page asks for an authenticator code.</summary>
    private async Task SecondFactorAsync(IPage page, TradingAccount a, CancellationToken ct)
    {
        var code = page.Locator(_o.CodeInputSelector).First;
        await code.WaitForAsync(new LocatorWaitForOptions { Timeout = _o.StepTimeoutSeconds * 1000f });
        var screen = Classify(await PromptTextAsync(page));
        if (screen == LoginScreen.BadPassword) throw new BrokerLoginException("Paytm rejected the login ID or password. Fix them in the account profile; not retried, to avoid locking the account.");
        if (screen == LoginScreen.SmsOtp)
        {
            var toAuthenticator = page.Locator(_o.SwitchToAuthenticatorSelector).First;
            if (!await toAuthenticator.IsVisibleAsync()) throw new BrokerLoginException(SmsOtpMessage);
            log.LogInformation("Login page offered SMS OTP; switching to the authenticator code");
            await toAuthenticator.ClickAsync();
            await page.WaitForLoadStateAsync();
            await code.WaitForAsync(new LocatorWaitForOptions { Timeout = _o.StepTimeoutSeconds * 1000f });
            if (Classify(await PromptTextAsync(page)) == LoginScreen.SmsOtp) throw new BrokerLoginException(SmsOtpMessage);
        }

        var totp = await ResolveAsync("{totp}", a, ct);
        // Six one-digit boxes: type into the first and let the page move focus along
        var boxes = page.Locator("input[maxlength='1']");
        if (await boxes.CountAsync() >= 4)
        {
            await boxes.First.ClickAsync();
            await page.Keyboard.TypeAsync(totp, new KeyboardTypeOptions { Delay = 60 });
        }
        else await code.FillAsync(totp);
    }

    private async Task ThrowIfDeadEndAsync(IPage page)
    {
        string text;
        try { text = await PromptTextAsync(page); } catch (PlaywrightException) { return; }
        switch (Classify(text))
        {
            case LoginScreen.SmsOtp when !await page.Locator(_o.SwitchToAuthenticatorSelector).First.IsVisibleAsync():
                throw new BrokerLoginException(SmsOtpMessage);
            case LoginScreen.BadPassword:
                throw new BrokerLoginException("Paytm rejected the login ID or password. Fix them in the account profile; not retried, to avoid locking the account.");
        }
    }

    public LoginScreen Classify(string prompt)
    {
        if (Regex.IsMatch(prompt, _o.BadPasswordPattern)) return LoginScreen.BadPassword;
        if (Regex.IsMatch(prompt, _o.AuthenticatorPattern)) return LoginScreen.Authenticator;
        if (Regex.IsMatch(prompt, _o.SmsOtpPattern)) return LoginScreen.SmsOtp;
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
