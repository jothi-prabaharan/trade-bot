using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Engine;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Auth;

/// <summary>
/// Auth worker timing. On trading days from mst.strategy_config.auth_login_time until the close, every account that
/// should hold today's token and does not is logged in: the daily login, then automatic retries
/// <see cref="AutoRetryInterval"/> apart (<see cref="MaxAutoRounds"/> a day) while the failures are ones a new attempt can
/// fix. Logins asked for on the dashboard or after Paytm refused a token (tradebot_login_requested) run at once, any day.
/// </summary>
public sealed class AuthScheduler(BrokerAuthService auth, IConfigRepository configs, IAccountRepository accounts, TimeProvider clock, ILogger<AuthScheduler> log)
{
    public static readonly TimeSpan ConfigRefresh = TimeSpan.FromMinutes(5);
    /// <summary>How often the accounts are looked over for a missing token.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    /// <summary>Between automatic logins of one account (each login already retries quick failures itself).</summary>
    public static readonly TimeSpan AutoRetryInterval = TimeSpan.FromMinutes(10);
    /// <summary>Automatic logins per account and day, the daily login included.</summary>
    public const int MaxAutoRounds = 3;

    private readonly ConcurrentQueue<long> _requests = new();
    // Automatic logins today per account; Stopped after a failure only a person can fix (password, PIN, TOTP secret, SMS OTP)
    private readonly Dictionary<long, (int Rounds, DateTime LastUtc, bool Stopped)> _auto = [];
    private StrategyConfig _strategy = new();
    private MarketCalendar _calendar = MarketCalendar.WeekendsOnly;
    private DateTime _configLoadedUtc = DateTime.MinValue;
    private DateTime _sweptUtc = DateTime.MinValue;
    private DateOnly? _day;

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

        var today = Ist.Today(clock);
        if (_day != today)
        {
            _day = today;
            _auto.Clear();
        }

        var requested = new HashSet<long>();
        while (_requests.TryDequeue(out var id)) requested.Add(id);
        foreach (var id in requested)
        {
            // Someone asked (or Paytm refused the token): the automatic retries start afresh after this login
            _auto.Remove(id);
            if (await auth.LoginAsync(id, ct) == LoginOutcome.NeedsAttention) _auto[id] = (0, nowUtc, true);
        }

        var time = Ist.Time(clock);
        if (time < _strategy.AuthLoginTime || _calendar.SessionFor(today) is not { } session || time >= session.CloseExclusive) return;
        if (nowUtc - _sweptUtc < SweepInterval) return;
        _sweptUtc = nowUtc;

        // The daily login, a restart's catch-up and the automatic retries: every account still without today's token
        var tokens = await accounts.LatestTokensAsync(ct);
        var sinceUtc = Ist.ToUtc(today, TimeOnly.MinValue);
        foreach (var a in (await accounts.ListAsync(activeOnly: true, ct)).Where(BrokerAuthService.NeedsLogin))
        {
            if (requested.Contains(a.AccountId) || HasTodaysToken(tokens.GetValueOrDefault(a.AccountId), sinceUtc, nowUtc)) continue;
            var (rounds, lastUtc, stopped) = _auto.GetValueOrDefault(a.AccountId);
            if (stopped || rounds >= MaxAutoRounds || rounds > 0 && nowUtc - lastUtc < AutoRetryInterval) continue;

            var retryAtUtc = rounds + 1 < MaxAutoRounds ? nowUtc + AutoRetryInterval : (DateTime?)null;
            var outcome = await auth.LoginAsync(a.AccountId, ct, retryAtUtc);
            _auto[a.AccountId] = (rounds + 1, nowUtc, outcome == LoginOutcome.NeedsAttention);
            if (outcome == LoginOutcome.Failed && retryAtUtc is { } at)
                log.LogInformation("Account {Account}: automatic login {Round}/{Max} failed; trying again at {At:HH:mm} IST",
                    a.AccountId, rounds + 1, MaxAutoRounds, FixedLimits.ToIst(at));
        }
    }

    /// <summary>A successful login made today whose token has not expired.</summary>
    public static bool HasTodaysToken(AccountAuthToken? t, DateTime sinceUtc, DateTime nowUtc) =>
        t is { LoginStatus: LoginStatus.Success } && t.CreatedAt >= sinceUtc && (t.TokenExpiry is not { } e || e > nowUtc);

    /// <summary>The next daily login after <paramref name="nowUtc"/>: auth_login_time on the next trading day that has it ahead.</summary>
    public static DateTime? NextDailyLoginUtc(DateTime nowUtc, TimeOnly loginTime, MarketCalendar calendar)
    {
        var today = DateOnly.FromDateTime(FixedLimits.ToIst(nowUtc));
        for (var d = today; d <= today.AddDays(14); d = d.AddDays(1))
            if (calendar.SessionFor(d) is not null && Ist.ToUtc(d, loginTime) is var at && at > nowUtc) return at;
        return null;
    }
}
