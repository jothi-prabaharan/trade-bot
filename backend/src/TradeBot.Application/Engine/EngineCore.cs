using System.Collections.Concurrent;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

public static class Ist
{
    public static DateTime Now(TimeProvider clock) => FixedLimits.ToIst(clock.GetUtcNow().UtcDateTime);
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(Now(clock));
    public static TimeOnly Time(TimeProvider clock) => TimeOnly.FromDateTime(Now(clock));
    public static DateTime ToUtc(DateOnly date, TimeOnly time) => DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Utc) - FixedLimits.IstOffset;
}

/// <summary>Snapshot of mst.* configuration the engine reads on every decision; reloaded on change notifications.</summary>
public sealed class ConfigCache(IConfigRepository configs, IAccountRepository accounts, IMarketRepository market)
{
    public StrategyConfig Strategy { get; private set; } = new();
    public GeminiNewsConfig Gemini { get; private set; } = new() { ModelName = "gemini-flash-latest" };
    public List<ChargeRate> ChargeRates { get; private set; } = [];
    public MarketCalendar Calendar { get; private set; } = MarketCalendar.WeekendsOnly;
    /// <summary>Active accounts: the ones the engine trades and the dashboard totals.</summary>
    public List<TradingAccount> Accounts { get; private set; } = [];
    /// <summary>Active and inactive, so the dashboard can still show (and re-activate) an inactive account picked on its own.</summary>
    public List<TradingAccount> AllAccounts { get; private set; } = [];
    public List<Instrument> Instruments { get; private set; } = [];
    public List<ChartPattern> Patterns { get; private set; } = [];
    public DateTime LoadedAtUtc { get; private set; }

    public event Action? Reloaded;

    public async Task ReloadAsync(CancellationToken ct)
    {
        Strategy = await configs.StrategyAsync(ct);
        Gemini = await configs.GeminiAsync(ct) ?? Gemini;
        ChargeRates = await configs.ChargeRatesAsync(ct);
        Calendar = new MarketCalendar(await configs.HolidaysAsync(ct));
        AllAccounts = await accounts.ListAsync(activeOnly: false, ct);
        Accounts = AllAccounts.Where(a => a.IsActive).ToList();
        Instruments = await market.InstrumentsAsync(tradableOnly: false, ct);
        Patterns = await configs.PatternsAsync(ct);
        LoadedAtUtc = DateTime.UtcNow;
        Reloaded?.Invoke();
    }

    public TradingAccount? Account(long id) => Accounts.FirstOrDefault(a => a.AccountId == id);
    public Instrument? Instrument(long id) => Instruments.FirstOrDefault(i => i.InstrumentId == id);

    /// <summary>Reference bracket policy for signals: the data-provider account, else the first active account.</summary>
    public AccountRiskConfig ReferenceRisk =>
        (Accounts.FirstOrDefault(a => a.IsDataProvider) ?? Accounts.FirstOrDefault())?.RiskConfig ?? new AccountRiskConfig();
}

/// <summary>Decrypted latest broker tokens per account (refreshed when the Auth worker stores new ones).</summary>
public sealed class TokenStore(IAccountRepository accounts, TimeProvider clock)
{
    private volatile Dictionary<long, AccountAuthToken> _tokens = new();

    public async Task ReloadAsync(CancellationToken ct) => _tokens = await accounts.LatestTokensAsync(ct);

    public AccountAuthToken? Latest(long accountId) => _tokens.GetValueOrDefault(accountId);

    public AccountAuthToken? Valid(long accountId) =>
        Latest(accountId) is { LoginStatus: LoginStatus.Success, XJwtToken.Length: > 0, TokenExpiry: { } exp } t && exp > clock.GetUtcNow().UtcDateTime ? t : null;

    public string? AccessToken(long accountId) => Valid(accountId)?.XJwtToken;
    public string? ReadToken(long accountId) => Valid(accountId) is { } t ? t.ReadAccessToken ?? t.XJwtToken : null;
    public string? PublicToken(long accountId) => Valid(accountId)?.PublicAccessToken;
}

public enum SeriesRole { Spot, Future, Option }

public sealed record ScripInfo(long SecurityId, long InstrumentId, SeriesRole Role, SecurityMaster? Contract);

/// <summary>Latest quote per security and what each subscribed security represents.</summary>
public sealed class MarketDataHub
{
    private readonly ConcurrentDictionary<long, Tick> _last = new();
    private readonly ConcurrentDictionary<long, ScripInfo> _scrips = new();

    public void Register(ScripInfo info) => _scrips[info.SecurityId] = info;
    public ScripInfo? Info(long securityId) => _scrips.GetValueOrDefault(securityId);
    public IReadOnlyCollection<ScripInfo> Scrips => _scrips.Values.ToList();

    public void OnTick(Tick t) => _last[t.SecurityId] = t;
    public Tick? Last(long securityId) => _last.TryGetValue(securityId, out var t) ? t : null;

    public decimal? UnderlyingPrice(long instrumentId)
    {
        var future = _scrips.Values.FirstOrDefault(s => s.InstrumentId == instrumentId && s.Role == SeriesRole.Future);
        var spot = _scrips.Values.FirstOrDefault(s => s.InstrumentId == instrumentId && s.Role == SeriesRole.Spot);
        return (spot is null ? null : Last(spot.SecurityId)?.Ltp) ?? (future is null ? null : Last(future.SecurityId)?.Ltp);
    }

    public decimal? FuturesPrice(long instrumentId) =>
        _scrips.Values.FirstOrDefault(s => s.InstrumentId == instrumentId && s.Role == SeriesRole.Future) is { } f ? Last(f.SecurityId)?.Ltp : null;

    /// <summary>Candle series a tick feeds: futures form the instrument's continuous series; options their own.</summary>
    public SeriesKey? SeriesFor(long securityId) => Info(securityId) switch
    {
        { Role: SeriesRole.Future } f => new SeriesKey(f.InstrumentId, null),
        { Role: SeriesRole.Option } o => new SeriesKey(o.InstrumentId, o.SecurityId),
        _ => null,
    };
}
