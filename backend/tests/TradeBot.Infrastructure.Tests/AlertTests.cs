using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using TradeBot.Application.Abstractions;
using TradeBot.Infrastructure.External;
using TradeBot.Infrastructure.External.WebPush;

namespace TradeBot.Infrastructure.Tests;

internal sealed class TestChannel(Func<int, CancellationToken, Task>? onSend = null) : IAlertSender
{
    private readonly List<(AlertKind Kind, string Html)> _got = [];
    public IReadOnlyList<(AlertKind Kind, string Html)> Got { get { lock (_got) return [.. _got]; } }

    public async Task SendAsync(AlertKind kind, string html, CancellationToken ct)
    {
        int n;
        lock (_got) n = _got.Count;
        if (onSend is not null) await onSend(n, ct);
        lock (_got) _got.Add((kind, html));
    }

    public async Task WaitForAsync(int count)
    {
        var sw = Stopwatch.StartNew();
        while (Got.Count < count)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"{Got.Count} of {count} alerts arrived");
            await Task.Delay(10);
        }
    }
}

public sealed class AlertDispatcherTests
{
    private static AlertDispatcher Dispatcher(params IAlertSender[] channels) =>
        new(channels, NullLogger<AlertDispatcher>.Instance, TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task Callers_never_wait_and_a_hung_channel_does_not_hold_up_the_others()
    {
        var hung = new TestChannel((_, ct) => Task.Delay(Timeout.Infinite, ct));
        var push = new TestChannel();
        var d = Dispatcher(hung, push);
        await d.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) await d.SendAsync(AlertKind.Critical, $"alert {i}", CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 100, $"SendAsync blocked for {sw.ElapsedMilliseconds} ms");

        await push.WaitForAsync(3);
        Assert.Equal(["alert 0", "alert 1", "alert 2"], push.Got.Select(g => g.Html));
        Assert.Empty(hung.Got);
        await d.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_channel_is_logged_and_skipped()
    {
        var broken = new TestChannel((_, _) => throw new HttpRequestException("telegram down"));
        var push = new TestChannel();
        var d = Dispatcher(broken, push);
        await d.StartAsync(CancellationToken.None);
        await d.SendAsync(AlertKind.Entry, "a", CancellationToken.None);
        await d.SendAsync(AlertKind.Exit, "b", CancellationToken.None);
        await push.WaitForAsync(2);
        Assert.Equal([AlertKind.Entry, AlertKind.Exit], push.Got.Select(g => g.Kind));
        await d.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Shutdown_delivers_what_is_still_queued()
    {
        var slow = new TestChannel((_, ct) => Task.Delay(30, ct));
        var d = Dispatcher(slow);
        await d.StartAsync(CancellationToken.None);
        for (var i = 0; i < 5; i++) await d.SendAsync(AlertKind.Info, $"eod {i}", CancellationToken.None);
        await d.StopAsync(CancellationToken.None);
        Assert.Equal(5, slow.Got.Count);
    }
}

public sealed class PushFormattingTests
{
    [Fact]
    public void Telegram_html_becomes_a_title_and_plain_body()
    {
        var html = "🟢 <b>ENTRY</b> 📝 PAPER · Paper &amp; Co\n<b>NIFTY 25000 CE</b> × 75 (1 lot)\nEntry ₹100.25 · SL ₹85.45 · Target ₹130.65";
        var (title, body) = PushText.FromAlert(AlertKind.Entry, html);
        Assert.Equal("🟢 ENTRY 📝 PAPER · Paper & Co", title);
        Assert.Equal("NIFTY 25000 CE × 75 (1 lot)\nEntry ₹100.25 · SL ₹85.45 · Target ₹130.65", body);
    }

    [Fact]
    public void A_one_line_alert_gets_a_title_for_its_kind_and_long_bodies_are_clipped()
    {
        Assert.Equal(("TradeBot alert", "⚠️ Stop-loss order rejected for trade 7"), PushText.FromAlert(AlertKind.Critical, "⚠️ Stop-loss order rejected for trade 7"));
        var (_, body) = PushText.FromAlert(AlertKind.Info, "EOD\n" + new string('x', 5000));
        Assert.Equal(1500, body.Length);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public void Payload_is_an_angular_service_worker_notification_that_opens_the_app()
    {
        var json = JsonNode.Parse(PushPayload.Json(AlertKind.Critical, "Kill switch", "Halted", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)))!["notification"]!;
        Assert.Equal("Kill switch", (string?)json["title"]);
        Assert.Equal("/icons/badge-96.png", (string?)json["badge"]);
        Assert.True((bool?)json["requireInteraction"]);
        Assert.Equal(1_700_000_000_000, (long?)json["timestamp"]);
        Assert.Equal("navigateLastFocusedOrOpen", (string?)json["data"]!["onActionClick"]!["default"]!["operation"]);

        var info = JsonNode.Parse(PushPayload.Json(AlertKind.Info, "t", "b", DateTimeOffset.UnixEpoch))!["notification"]!.AsObject();
        Assert.False(info.ContainsKey("requireInteraction"));
        Assert.False(info.ContainsKey("vibrate"));
    }

    [Fact]
    public void Generated_vapid_keys_have_the_browser_format_and_sign()
    {
        var (publicKey, privateKey) = VapidKeys.Generate();
        var point = Base64Url.DecodeFromChars(publicKey);
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
        Assert.Equal(32, Base64Url.DecodeFromChars(privateKey).Length);

        using var vapid = new VapidAuthentication(publicKey, privateKey) { Subject = "mailto:ops@example.com" };
        var header = vapid.GetVapidSchemeAuthenticationHeaderValueParameter("https://fcm.googleapis.com");
        Assert.StartsWith("t=", header);
        Assert.Contains($"k={publicKey}", header);
    }
}
