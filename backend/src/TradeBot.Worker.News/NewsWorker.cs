using TradeBot.Application.Abstractions;
using TradeBot.Application.News;

namespace TradeBot.Worker.News;

/// <summary>Drives <see cref="NewsScheduler"/> every 5 s and feeds it spike / manual scan requests from the event bus.</summary>
public sealed class NewsWorker(NewsScheduler scheduler, IEventBus bus, ILogger<NewsWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bus.Subscribe(EventChannels.NewsCheckRequested, (p, _) => { scheduler.Enqueue(p); return Task.CompletedTask; });
        bus.Subscribe(EventChannels.ConfigChanged, (_, _) => { scheduler.InvalidateConfig(); return Task.CompletedTask; });
        log.LogInformation("News worker started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try { await scheduler.TickAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "News scheduler tick failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
