using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;

namespace TradeBot.Api.Hosting;

/// <summary>Runs the trading engine loops inside the API process: feed pump, 1 s scheduler, REAL order polling, scanner, candle persistence.</summary>
public sealed class EngineHostedService(
    EngineOrchestrator orchestrator, IMarketFeed feed, MarketDataHub hub, PaperGateway paper, RealGateway real,
    CandlePipeline candles, TradeManager trades, PatternScanner scanner, IEventBus bus, ConfigCache config,
    ILogger<EngineHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bus.Subscribe(EventChannels.TokensUpdated, (_, c) => orchestrator.OnTokensUpdatedAsync(c));
        bus.Subscribe(EventChannels.ConfigChanged, (_, c) => config.ReloadAsync(c));

        await orchestrator.StartAsync(ct);
        log.LogInformation("Engine started: {Accounts} account(s), {Trades} recovered open trade(s)", config.Accounts.Count, trades.OpenTrades.Count);

        await Task.WhenAll(
            Loop("feed pump", FeedPumpAsync, ct),
            Loop("scheduler", async c => { while (!c.IsCancellationRequested) { await Guard(() => orchestrator.TickAsync(c)); await Task.Delay(1000, c); } }, ct),
            Loop("order poller", async c => { while (!c.IsCancellationRequested) { if (real.HasWorkingOrders) await Guard(() => real.PollAsync(c)); await Task.Delay(1000, c); } }, ct),
            Loop("pattern scanner", c => scanner.RunAsync(candles.ClosedBars, c), ct),
            Loop("candle writer", candles.RunPersistenceAsync, ct));
    }

    private async Task FeedPumpAsync(CancellationToken ct)
    {
        await foreach (var t in feed.Ticks.ReadAllAsync(ct))
        {
            try
            {
                hub.OnTick(t);
                paper.OnTick(t.SecurityId, t.Ltp, t.TimeUtc);
                candles.OnTick(t);
                trades.OnTick(t);
            }
            catch (Exception e) { log.LogError(e, "Tick {Security} processing failed", t.SecurityId); }
        }
    }

    private async Task Guard(Func<Task> step)
    {
        try { await step(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { log.LogError(e, "Engine step failed"); }
    }

    private async Task Loop(string name, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await body(ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                log.LogCritical(e, "Engine loop {Loop} crashed; restarting in 3 s", name);
                await Task.Delay(3000, ct);
            }
        }
    }
}

/// <summary>
/// /ws/dashboard: pushes {"type":"overview"} once a second when it changes and {"type":"events"} for each batch of new
/// trn.system_events (toasts). Clients may send {"scope":"real"}.
/// </summary>
public static class DashboardSocket
{
    public static async Task HandleAsync(HttpContext ctx, DashboardService dashboard, ITradingRepository trading, ILogger<DashboardService> log)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var scope = ctx.Request.Query["scope"].FirstOrDefault() ?? "all";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) { await cts.CancelAsync(); return; }
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
                    if (doc.RootElement.TryGetProperty("scope", out var s) && s.GetString() is { Length: > 0 } next) scope = next;
                }
                catch (JsonException) { /* ignore malformed client messages */ }
            }
        }, cts.Token);

        string? last = null;
        try
        {
            var lastEventId = (await trading.EventsAsync(1, cts.Token)).FirstOrDefault()?.EventId ?? 0;
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(new { type = "overview", data = await dashboard.BuildAsync(scope, cts.Token) }, ApiJson.Options);
                if (json != last)
                {
                    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
                    last = json;
                }
                if (await trading.EventsAfterAsync(lastEventId, 100, cts.Token) is { Count: > 0 } events)
                {
                    lastEventId = events[^1].EventId;
                    var payload = JsonSerializer.Serialize(new { type = "events", data = events }, ApiJson.Options);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cts.Token);
                }
                await Task.Delay(1000, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e) { log.LogDebug(e, "Dashboard socket closed"); }
        await cts.CancelAsync();
        try { await reader; } catch { /* reader ends with the socket */ }
        // Complete the close handshake the client started (sent only after the push loop has stopped)
        if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch (WebSocketException) { /* client already gone */ }
    }
}
