using TradeBot.Application.Abstractions;
using TradeBot.Application.Auth;

namespace TradeBot.Worker.Auth;

/// <summary>Drives <see cref="AuthScheduler"/> every 5 s and feeds it dashboard login requests from the event bus.</summary>
public sealed class AuthWorker(AuthScheduler scheduler, IEventBus bus, ILogger<AuthWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bus.Subscribe(EventChannels.LoginRequested, (p, _) => { scheduler.Enqueue(p); return Task.CompletedTask; });
        bus.Subscribe(EventChannels.ConfigChanged, (_, _) => { scheduler.InvalidateConfig(); return Task.CompletedTask; });
        log.LogInformation("Auth worker started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try { await scheduler.TickAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Auth scheduler tick failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
