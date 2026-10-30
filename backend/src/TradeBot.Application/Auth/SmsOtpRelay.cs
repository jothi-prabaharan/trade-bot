using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Application.Auth;

/// <summary>
/// Auth worker side of the dashboard OTP box: announces the prompt (event bus + alert), then waits for the code the
/// API relays back. One prompt per account at a time; the API forgets it on <see cref="EventChannels.OtpClosed"/>.
/// </summary>
public sealed partial class SmsOtpRelay : ISmsOtpPrompt
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _waiting = new();
    private readonly IEventBus _bus;
    private readonly IAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<SmsOtpRelay> _log;

    public SmsOtpRelay(IEventBus bus, IAlertSender alerts, TimeProvider clock, ILogger<SmsOtpRelay> log)
    {
        (_bus, _alerts, _clock, _log) = (bus, alerts, clock, log);
        bus.Subscribe(EventChannels.OtpSubmitted, (payload, _) => { Deliver(payload); return Task.CompletedTask; });
    }

    public static bool IsOtp(string? code) => code is not null && OtpRegex().IsMatch(code);

    public void Deliver(string payload)
    {
        var parts = payload.Split(':', 2);
        if (parts.Length == 2 && long.TryParse(parts[0], out var id) && IsOtp(parts[1]) && _waiting.TryGetValue(id, out var tcs))
            tcs.TrySetResult(parts[1]);
    }

    public async Task<string?> AskAsync(TradingAccount account, string? sentTo, string? note, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[account.AccountId] = tcs;
        try
        {
            var prompt = new OtpPrompt(account.AccountId, _clock.GetUtcNow().UtcDateTime + timeout, sentTo, note);
            await _bus.PublishAsync(EventChannels.OtpRequested, JsonSerializer.Serialize(prompt), ct);
            await _alerts.SendAsync(AlertKind.Critical,
                $"📲 Paytm sent an OTP{(sentTo is null ? "" : $" to {WebUtility.HtmlEncode(sentTo)}")} for <b>{WebUtility.HtmlEncode(account.AccountName)}</b>. " +
                $"Enter it on the dashboard within {(int)timeout.TotalMinutes} min so the bot can finish logging in." +
                (note is null ? "" : $"\n{WebUtility.HtmlEncode(note)}"), ct);
            _log.LogInformation("Waiting up to {Timeout} for the SMS OTP of account {Account}", timeout, account.AccountId);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, _clock, ct));
            ct.ThrowIfCancellationRequested();
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            _waiting.TryRemove(new(account.AccountId, tcs));
            try { await _bus.PublishAsync(EventChannels.OtpClosed, account.AccountId.ToString(), CancellationToken.None); }
            catch (Exception e) { _log.LogWarning(e, "Could not close the OTP prompt of account {Account}", account.AccountId); }
        }
    }

    [GeneratedRegex(@"^\d{4,8}$")]
    private static partial Regex OtpRegex();
}

/// <summary>API side: the OTP prompts the Auth worker is waiting on, shown on the account cards.</summary>
public sealed class OtpPrompts(TimeProvider clock, ILogger<OtpPrompts> log)
{
    private readonly ConcurrentDictionary<long, OtpPrompt> _open = new();

    public void OnRequested(string payload)
    {
        try { if (JsonSerializer.Deserialize<OtpPrompt>(payload) is { } p) _open[p.AccountId] = p; }
        catch (JsonException e) { log.LogWarning(e, "Ignoring malformed OTP prompt {Payload}", payload); }
    }

    public void OnClosed(string payload)
    {
        if (long.TryParse(payload, out var id)) _open.TryRemove(id, out _);
    }

    public OtpPrompt? For(long accountId) =>
        _open.TryGetValue(accountId, out var p) && p.UntilUtc > clock.GetUtcNow().UtcDateTime ? p : null;
}
