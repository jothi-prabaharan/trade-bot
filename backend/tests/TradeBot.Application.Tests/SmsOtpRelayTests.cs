using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Application.Tests;

/// <summary>Both processes on one in-memory bus: the Auth worker's relay and the API's prompt list.</summary>
internal sealed class MemoryBus : IEventBus
{
    private readonly Dictionary<string, List<Func<string, CancellationToken, Task>>> _handlers = [];
    public List<(string Channel, string Payload)> Published { get; } = [];

    public async Task PublishAsync(string channel, string payload, CancellationToken ct)
    {
        Published.Add((channel, payload));
        foreach (var h in _handlers.GetValueOrDefault(channel) ?? []) await h(payload, ct);
    }

    public void Subscribe(string channel, Func<string, CancellationToken, Task> handler)
    {
        if (!_handlers.TryGetValue(channel, out var list)) _handlers[channel] = list = [];
        list.Add(handler);
    }
}

public sealed class SmsOtpRelayTests
{
    private readonly MemoryBus _bus = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 2, 3, 15, 0, TimeSpan.Zero));
    private readonly Stub<IAlertSender> _alerts = Stub<IAlertSender>.Create();
    private readonly OtpPrompts _prompts;
    private readonly SmsOtpRelay _relay;
    private static readonly TradingAccount Account = new() { AccountId = 7, AccountName = "Real", ClientId = "C7", UserName = "u" };

    public SmsOtpRelayTests()
    {
        _prompts = new OtpPrompts(_clock, NullLogger<OtpPrompts>.Instance);
        _bus.Subscribe(EventChannels.OtpRequested, (p, _) => { _prompts.OnRequested(p); return Task.CompletedTask; });
        _bus.Subscribe(EventChannels.OtpClosed, (p, _) => { _prompts.OnClosed(p); return Task.CompletedTask; });
        _relay = new SmsOtpRelay(_bus, _alerts.Object, _clock, NullLogger<SmsOtpRelay>.Instance);
    }

    [Fact]
    public async Task The_dashboard_sees_the_prompt_and_its_otp_reaches_the_waiting_login()
    {
        var ask = _relay.AskAsync(Account, "******3210", null, TimeSpan.FromMinutes(3), CancellationToken.None);
        var shown = _prompts.For(7);
        Assert.NotNull(shown);
        Assert.Equal("******3210", shown.SentTo);
        Assert.Contains("******3210", (string)Assert.Single(_alerts.Calls(nameof(IAlertSender.SendAsync)))[1]!);

        await _bus.PublishAsync(EventChannels.OtpSubmitted, "7:482913", CancellationToken.None);
        Assert.Equal("482913", await ask);
        Assert.Null(_prompts.For(7));
    }

    [Theory]
    [InlineData("8:482913")]   // another account
    [InlineData("7:12ab56")]   // not digits
    [InlineData("7:12")]       // too short
    public async Task Otps_for_another_account_or_malformed_are_ignored(string payload)
    {
        var ask = _relay.AskAsync(Account, null, null, TimeSpan.FromMinutes(3), CancellationToken.None);
        await _bus.PublishAsync(EventChannels.OtpSubmitted, payload, CancellationToken.None);
        Assert.False(ask.IsCompleted);
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await ask);
    }

    [Fact]
    public async Task An_unanswered_prompt_times_out_and_disappears_from_the_dashboard()
    {
        var ask = _relay.AskAsync(Account, null, null, TimeSpan.FromMinutes(3), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await ask);
        Assert.Null(_prompts.For(7));
        Assert.Contains(_bus.Published, p => p.Channel == EventChannels.OtpClosed && p.Payload == "7");
    }
}
