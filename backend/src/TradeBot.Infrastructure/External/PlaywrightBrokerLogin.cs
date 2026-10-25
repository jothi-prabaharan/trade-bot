using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Security;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Infrastructure.External;

public sealed record LoginStep(string Action, string Selector, string? Value = null, bool Optional = false);

/// <summary>
/// Bound from "BrokerLogin". The step list is data so the flow can follow login-page changes without a
/// rebuild. Placeholders: {loginId} {password} {totp} {pin}.
/// </summary>
public sealed class BrokerLoginOptions
{
    public string? ChromiumExecutablePath { get; set; }
    public bool Headless { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 90;
    public int StepTimeoutSeconds { get; set; } = 15;
    public string ScreenshotDirectory { get; set; } = "/var/lib/tradebot/auth-failures";

    public List<LoginStep> Steps { get; set; } =
    [
        new("fill", "input[type='tel'], input[type='email'], input[name*='mobile' i], input[name*='email' i], input[name*='user' i]", "{loginId}"),
        new("click", "button[type='submit'], button:has-text('Proceed'), button:has-text('Next'), button:has-text('Continue')", Optional: true),
        new("fill", "input[type='password']", "{password}"),
        new("click", "button[type='submit'], button:has-text('Login'), button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
        new("fill", "input[autocomplete='one-time-code'], input[name*='otp' i], input[name*='totp' i], input[inputmode='numeric']", "{totp}"),
        new("click", "button[type='submit'], button:has-text('Verify'), button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
        new("fill", "input[name*='pin' i], input[name*='passcode' i]", "{pin}", Optional: true),
        new("click", "button[type='submit'], button:has-text('Proceed'), button:has-text('Continue')", Optional: true),
    ];
}

/// <summary>Headless Chromium login that captures the requestToken from the broker's redirect.</summary>
public sealed partial class PlaywrightBrokerLogin(IOptions<BrokerLoginOptions> options, TimeProvider clock, ILogger<PlaywrightBrokerLogin> log) : IBrokerLoginAutomation
{
    private readonly BrokerLoginOptions _o = options.Value;

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
                catch (Exception e) when (step.Optional && e is PlaywrightException or TimeoutException)
                {
                    log.LogDebug("Optional login step {Action} {Selector} skipped", step.Action, step.Selector);
                }
            }
            var done = await Task.WhenAny(token.Task, Task.Delay(TimeSpan.FromSeconds(_o.TimeoutSeconds), ct));
            if (done != token.Task) throw new TimeoutException("Login did not redirect with a requestToken.");
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
        switch (step.Action.ToLowerInvariant())
        {
            case "fill":
                var value = await ResolveAsync(step.Value ?? "", a, ct);
                if (string.IsNullOrEmpty(value)) { if (step.Optional) return; throw new InvalidOperationException($"No value for login step {step.Selector}"); }
                await locator.FillAsync(value);
                break;
            case "click":
                await locator.ClickAsync();
                break;
            case "press":
                await locator.PressAsync(step.Value ?? "Enter");
                break;
            case "waitfor":
                await locator.WaitForAsync();
                break;
            default:
                throw new InvalidOperationException($"Unknown login step action '{step.Action}'.");
        }
    }

    private async Task<string> ResolveAsync(string template, TradingAccount a, CancellationToken ct)
    {
        if (template.Contains("{totp}"))
        {
            // Avoid submitting a code that expires mid-request
            if (Totp.SecondsRemaining(clock.GetUtcNow().UtcDateTime) < 5) await Task.Delay(TimeSpan.FromSeconds(6), ct);
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
