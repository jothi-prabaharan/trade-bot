using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;

namespace TradeBot.Infrastructure.External;

/// <summary>
/// The IAlertSender every host uses: queues the alert and returns at once, then delivers it to each channel
/// (Telegram, Web Push) in the background. A slow or unreachable channel can never delay the trading path,
/// e.g. exiting a position after its stop-loss order was rejected, and never holds up the other channels:
/// each one has its own queue. Pending alerts are delivered on shutdown.
/// </summary>
public sealed class AlertDispatcher : IHostedService, IAlertSender
{
    private readonly (IAlertSender Channel, Channel<(AlertKind Kind, string Html)> Queue)[] _lanes;
    private readonly TimeSpan _channelTimeout;
    private readonly ILogger<AlertDispatcher> _log;
    private Task _running = Task.CompletedTask;

    public AlertDispatcher(IEnumerable<IAlertSender> channels, ILogger<AlertDispatcher> log, TimeSpan? channelTimeout = null)
    {
        _log = log;
        _channelTimeout = channelTimeout ?? TimeSpan.FromSeconds(20);
        _lanes = channels.Select(c => (c, Channel.CreateBounded<(AlertKind Kind, string Html)>(
            // An alert storm drops the oldest pending alert rather than ever blocking a caller
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            dropped => log.LogWarning("{Channel} queue full; dropped a {Kind} alert", c.GetType().Name, dropped.Kind)))).ToArray();
    }

    public Task SendAsync(AlertKind kind, string html, CancellationToken ct)
    {
        foreach (var (channel, queue) in _lanes)
            if (!queue.Writer.TryWrite((kind, html))) _log.LogWarning("{Channel}: {Kind} alert after shutdown not sent", channel.GetType().Name, kind);
        return Task.CompletedTask;
    }

    // Not BackgroundService: its ExecuteAsync never runs if the host stops before the thread pool starts it,
    // which would drop queued alerts. These lanes always start, and StopAsync waits for them to drain.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _running = Task.WhenAll(_lanes.Select(l => Task.Run(() => DrainAsync(l.Channel, l.Queue), CancellationToken.None)));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (_, queue) in _lanes) queue.Writer.TryComplete();
        await _running.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task DrainAsync(IAlertSender channel, Channel<(AlertKind Kind, string Html)> queue)
    {
        await foreach (var (kind, html) in queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            using var timeout = new CancellationTokenSource(_channelTimeout);
            try
            {
                await channel.SendAsync(kind, html, timeout.Token);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "{Channel} could not deliver a {Kind} alert", channel.GetType().Name, kind);
            }
        }
    }
}
