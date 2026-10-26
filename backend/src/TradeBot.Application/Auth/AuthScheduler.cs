using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Auth;

/// <summary>
/// Auth worker timing: the daily login of every account at mst.strategy_config.auth_login_time on trading days,
/// plus on-demand logins requested from the dashboard (tradebot_login_requested).
/// </summary>
public sealed class AuthScheduler(BrokerAuthService auth, IConfigRepository configs, IAccountRepository accounts, TimeProvider clock, ILogger<AuthScheduler> log)
{
    public static readonly TimeSpan ConfigRefresh = TimeSpan.FromMinutes(5);

    private readonly ConcurrentQueue<long> _requests = new();
    private StrategyConfig _strategy = new();
    private MarketCalendar _calendar = MarketCalendar.WeekendsOnly;
    private DateTime _configLoadedUtc = DateTime.MinValue;
    private DateOnly? _dailyDoneFor;

    public int Pending => _requests.Count;

    public void Enqueue(string payload)
    {
        if (long.TryParse(payload, out var id) && id > 0) _requests.Enqueue(id);
        else log.LogWarning("Ignoring malformed login request payload {Payload}", payload);
    }

    public void InvalidateConfig() => _configLoadedUtc = DateTime.MinValue;

    public async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        if (nowUtc - _configLoadedUtc >= ConfigRefresh)
        {
            _strategy = await configs.StrategyAsync(ct);
            _calendar = new MarketCalendar(await configs.HolidaysAsync(ct));
            _configLoadedUtc = nowUtc;
        }

        var requested = new HashSet<long>();
        while (_requests.TryDequeue(out var id)) requested.Add(id);
        foreach (var id in requested) await auth.LoginAsync(id, ct);

        var today = Ist.Today(clock);
        if (_dailyDoneFor == today || Ist.Time(clock) < _strategy.AuthLoginTime) return;
        _dailyDoneFor = today;
        if (_calendar.SessionFor(today) is not { } session || Ist.Time(clock) >= session.CloseExclusive) return;

        // After a restart, only accounts still without today's successful token are logged in again
        var tokens = await accounts.LatestTokensAsync(ct);
        var sinceUtc = Ist.ToUtc(today, TimeOnly.MinValue);
        foreach (var a in (await accounts.ListAsync(activeOnly: true, ct)).Where(BrokerAuthService.NeedsLogin))
        {
            if (requested.Contains(a.AccountId)) continue;
            if (tokens.GetValueOrDefault(a.AccountId) is { LoginStatus: LoginStatus.Success } t && t.CreatedAt >= sinceUtc) continue;
            await auth.LoginAsync(a.AccountId, ct);
        }
    }
}
