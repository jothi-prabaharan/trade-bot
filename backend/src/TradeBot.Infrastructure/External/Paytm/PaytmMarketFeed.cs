using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;

namespace TradeBot.Infrastructure.External.Paytm;

/// <summary>
/// Paytm broadcast WebSocket with automatic reconnect and resubscribe. Subscriptions are sharded across
/// connections of at most <see cref="PaytmMoneyOptions.MaxSubscriptionsPerConnection"/> scrips.
/// </summary>
public sealed class PaytmMarketFeed(IOptions<PaytmMoneyOptions> options, ILogger<PaytmMarketFeed> log) : IMarketFeed
{
    private sealed class Shard
    {
        public readonly HashSet<FeedSubscription> Subs = [];
        public ClientWebSocket? Socket;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    private readonly PaytmMoneyOptions _o = options.Value;
    private readonly Channel<Tick> _ticks = Channel.CreateBounded<Tick>(new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<Shard> _shards = [];
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private string? _token;
    private long _lastTickTicks;

    public ChannelReader<Tick> Ticks => _ticks.Reader;
    public bool IsConnected { get { lock (_lock) return _shards.Count > 0 && _shards.All(s => s.Socket?.State == WebSocketState.Open); } }
    public DateTime? LastTickUtc => Interlocked.Read(ref _lastTickTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    public Task StartAsync(string publicAccessToken, CancellationToken ct)
    {
        _token = publicAccessToken;
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock)
            foreach (var shard in _shards) _ = RunShardAsync(shard, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task SubscribeAsync(IReadOnlyCollection<FeedSubscription> scrips, CancellationToken ct)
    {
        var added = new List<(Shard Shard, List<FeedSubscription> Subs)>();
        lock (_lock)
        {
            foreach (var s in scrips.Where(s => !_shards.Any(x => x.Subs.Contains(s))))
            {
                var shard = _shards.FirstOrDefault(x => x.Subs.Count < _o.MaxSubscriptionsPerConnection);
                if (shard is null)
                {
                    _shards.Add(shard = new Shard());
                    if (_cts is not null) _ = RunShardAsync(shard, _cts.Token);
                }
                shard.Subs.Add(s);
                var entry = added.FirstOrDefault(a => a.Shard == shard);
                if (entry.Shard is null) added.Add((shard, [s])); else entry.Subs.Add(s);
            }
        }
        foreach (var (shard, subs) in added) await SendPreferencesAsync(shard, subs, "ADD", ct);
    }

    public async Task UnsubscribeAsync(IReadOnlyCollection<FeedSubscription> scrips, CancellationToken ct)
    {
        var removed = new List<(Shard, List<FeedSubscription>)>();
        lock (_lock)
            foreach (var shard in _shards)
            {
                var hit = scrips.Where(shard.Subs.Remove).ToList();
                if (hit.Count > 0) removed.Add((shard, hit));
            }
        foreach (var (shard, subs) in removed) await SendPreferencesAsync(shard, subs, "REMOVE", ct);
    }

    private async Task RunShardAsync(Shard shard, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            try
            {
                await ws.ConnectAsync(new Uri($"{_o.WebSocketUrl}?x_jwt_token={Uri.EscapeDataString(_token ?? "")}"), ct);
                shard.Socket = ws;
                delay = TimeSpan.FromSeconds(1);
                List<FeedSubscription> current;
                lock (_lock) current = [.. shard.Subs];
                await SendPreferencesAsync(shard, current, "ADD", ct);
                log.LogInformation("Market feed shard connected with {Count} scrips", current.Count);

                using var frame = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    frame.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    if (r.MessageType == WebSocketMessageType.Binary)
                    {
                        var now = DateTime.UtcNow;
                        foreach (var t in PaytmTickParser.Parse(frame.GetBuffer().AsSpan(0, (int)frame.Length), now)) _ticks.Writer.TryWrite(t);
                        Interlocked.Exchange(ref _lastTickTicks, now.Ticks);
                    }
                    else log.LogDebug("Feed text message: {Text}", Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
                    frame.SetLength(0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning(e, "Market feed shard disconnected; reconnecting in {Delay}", delay);
            }
            finally { shard.Socket = null; }

            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }

    private async Task SendPreferencesAsync(Shard shard, List<FeedSubscription> subs, string action, CancellationToken ct)
    {
        if (subs.Count == 0 || shard.Socket is not { State: WebSocketState.Open } ws) return; // sent on (re)connect
        var payload = JsonSerializer.SerializeToUtf8Bytes(subs.Select(s => new
        {
            actionType = action,
            modeType = "FULL",
            scripType = PaytmMoneyClient.ScripTypeName(s.Type),
            exchangeType = s.Exchange,
            scripId = s.SecurityId.ToString(),
        }));
        await shard.SendLock.WaitAsync(ct);
        try { await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct); }
        finally { shard.SendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _ticks.Writer.TryComplete();
    }
}
