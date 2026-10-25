using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradeBot.Application.Abstractions;

namespace TradeBot.Infrastructure.External;

/// <summary>
/// PostgreSQL LISTEN/NOTIFY bus between the API+Engine, News and Auth processes. The listener holds one
/// dedicated connection and reconnects (re-issuing LISTEN) after any failure.
/// </summary>
public sealed class PgEventBus(string connectionString, ILogger<PgEventBus> log) : BackgroundService, IEventBus
{
    private readonly ConcurrentDictionary<string, List<Func<string, CancellationToken, Task>>> _handlers = new();

    public async Task PublishAsync(string channel, string payload, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@c, @p)", conn);
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.AddWithValue("p", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Subscribe(string channel, Func<string, CancellationToken, Task> handler) =>
        _handlers.AddOrUpdate(channel, _ => [handler], (_, list) => { lock (list) list.Add(handler); return list; });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                conn.Notification += (_, e) => _ = DispatchAsync(e.Channel, e.Payload, ct);
                var listening = new HashSet<string>();
                while (!ct.IsCancellationRequested)
                {
                    // Channels subscribed after start are picked up within a second
                    foreach (var channel in _handlers.Keys.Where(listening.Add))
                    {
                        await using var listen = new NpgsqlCommand($"LISTEN \"{channel.Replace("\"", "")}\"", conn);
                        await listen.ExecuteNonQueryAsync(ct);
                        log.LogInformation("Event bus listening on {Channel}", channel);
                    }
                    await conn.WaitAsync(TimeSpan.FromSeconds(1), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning(e, "Event bus connection lost; reconnecting");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task DispatchAsync(string channel, string payload, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(channel, out var list)) return;
        Func<string, CancellationToken, Task>[] snapshot;
        lock (list) snapshot = [.. list];
        foreach (var h in snapshot)
        {
            try { await h(payload, ct); }
            catch (Exception e) { log.LogError(e, "Event handler for {Channel} failed", channel); }
        }
    }
}
