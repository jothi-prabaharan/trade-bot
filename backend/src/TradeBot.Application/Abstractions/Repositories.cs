using TradeBot.Application.Market;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Abstractions;

/// <summary>mst.trading_accounts, mst.account_risk_config, mst.account_auth_tokens.</summary>
public interface IAccountRepository
{
    Task<List<TradingAccount>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<TradingAccount?> GetAsync(long accountId, CancellationToken ct);
    Task<TradingAccount> AddAsync(TradingAccount account, CancellationToken ct);
    Task UpdateAsync(TradingAccount account, CancellationToken ct);
    Task UpdateRiskConfigAsync(AccountRiskConfig config, CancellationToken ct);
    Task SetTradeModeAsync(long accountId, TradeMode mode, string changedBy, DateTime nowUtc, CancellationToken ct);
    Task UpdateFundsAsync(long accountId, decimal available, decimal utilised, decimal? opening, DateTime syncedUtc, CancellationToken ct);
    Task<AccountAuthToken?> LatestTokenAsync(long accountId, CancellationToken ct);
    Task<Dictionary<long, AccountAuthToken>> LatestTokensAsync(CancellationToken ct);
    Task AddTokenAsync(AccountAuthToken token, CancellationToken ct);
}

/// <summary>mst.strategy_config, mst.gemini_news_config, mst.charge_rates, mst.market_holidays, mst.chart_patterns, mst.alert_channels.</summary>
public interface IConfigRepository
{
    Task<StrategyConfig> StrategyAsync(CancellationToken ct);
    Task SaveStrategyAsync(StrategyConfig config, CancellationToken ct);
    Task<GeminiNewsConfig?> GeminiAsync(CancellationToken ct);
    Task SaveGeminiAsync(GeminiNewsConfig config, CancellationToken ct);
    Task<List<ChargeRate>> ChargeRatesAsync(CancellationToken ct);
    Task<List<MarketHoliday>> HolidaysAsync(CancellationToken ct);
    Task<MarketHoliday> SaveHolidayAsync(MarketHoliday holiday, CancellationToken ct);
    Task DeleteHolidayAsync(long holidayId, CancellationToken ct);
    Task<List<ChartPattern>> PatternsAsync(CancellationToken ct);
    Task<ChartPattern?> PatternAsync(long patternId, CancellationToken ct);
    Task UpdatePatternAsync(ChartPattern pattern, CancellationToken ct);
    Task<int> AddMissingPatternsAsync(IEnumerable<ChartPattern> patterns, CancellationToken ct);
    Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken ct);
}

/// <summary>mst.instruments, mst.security_master, mst.stock_companies, trn.ohlcv_candles, trn.daily_watchlist.</summary>
public interface IMarketRepository
{
    Task<List<Instrument>> InstrumentsAsync(bool tradableOnly, CancellationToken ct);
    Task<List<SecurityMaster>> ContractsAsync(long instrumentId, DateOnly fromExpiry, CancellationToken ct);
    Task<SecurityMaster?> SecurityAsync(long securityId, CancellationToken ct);
    Task<List<SecurityMaster>> SecuritiesAsync(IEnumerable<long> securityIds, CancellationToken ct);
    Task<DateOnly?> LastScripSyncAsync(CancellationToken ct);
    Task<int> UpsertSecuritiesAsync(IReadOnlyCollection<SecurityMaster> rows, CancellationToken ct);
    Task<List<StockCompany>> CompaniesAsync(CancellationToken ct);
    Task SaveCandlesAsync(IReadOnlyCollection<ClosedBar> bars, CancellationToken ct);
    Task<List<Bar>> RecentBarsAsync(SeriesKey series, Timeframe timeframe, DateTime fromUtc, CancellationToken ct);
    Task<List<DailyWatchlist>> WatchlistAsync(DateOnly date, CancellationToken ct);
    Task UpsertWatchlistAsync(IEnumerable<DailyWatchlist> rows, CancellationToken ct);
}

/// <summary>trn.company_daily_news, trn.daily_market_sentiment, trn.detected_patterns, trn.signals.</summary>
public interface IIntelligenceRepository
{
    Task<List<NewsImpact>> NewsImpactsAsync(DateOnly date, Instrument instrument, CancellationToken ct);
    Task<DailyMarketSentiment?> SentimentAsync(DateOnly date, long instrumentId, CancellationToken ct);
    Task<List<DailyMarketSentiment>> SentimentsAsync(DateOnly date, CancellationToken ct);
    Task<CompanyDailyNews?> TopCatalystAsync(DateOnly date, long instrumentId, MarketBias bias, CancellationToken ct);
    Task<List<CompanyDailyNews>> NewsAsync(DateOnly date, long? instrumentId, int limit, CancellationToken ct);
    Task<int> AddNewsAsync(IReadOnlyCollection<CompanyDailyNews> items, CancellationToken ct);
    Task UpsertSentimentAsync(DailyMarketSentiment sentiment, CancellationToken ct);
    Task<DetectedPattern> AddDetectionAsync(DetectedPattern detection, CancellationToken ct);
    Task<List<DetectedPattern>> DetectionsAsync(DateOnly date, CancellationToken ct);
    Task<Signal> AddSignalAsync(Signal signal, CancellationToken ct);
    Task UpdateSignalStatusAsync(long signalId, SignalStatus status, string? reason, CancellationToken ct);
    Task<List<Signal>> SignalsAsync(DateOnly date, CancellationToken ct);
}

/// <summary>trn.orders, trn.daily_trades, trn.daily_account_pnl, trn.daily_bot_state, trn.system_events.</summary>
public interface ITradingRepository
{
    Task<Order> AddOrderAsync(Order order, CancellationToken ct);
    Task UpdateOrderAsync(Order order, CancellationToken ct);
    Task<List<Order>> WorkingOrdersAsync(CancellationToken ct);
    Task<bool> HasOrderForSignalAsync(long accountId, long signalId, CancellationToken ct);
    Task<List<Order>> OrdersForEntryAsync(long entryOrderId, CancellationToken ct);
    Task<DailyTrade> AddTradeAsync(DailyTrade trade, CancellationToken ct);
    Task UpdateTradeAsync(DailyTrade trade, CancellationToken ct);
    Task<DailyTrade?> TradeAsync(long tradeId, CancellationToken ct);
    Task<List<DailyTrade>> OpenTradesAsync(CancellationToken ct);
    Task<List<DailyTrade>> TradesAsync(DateOnly date, long? accountId, CancellationToken ct);
    Task<int> TradesTodayAsync(long accountId, DateOnly date, CancellationToken ct);
    Task<int> OpenTradeCountAsync(long accountId, CancellationToken ct);
    Task<DateTime?> LastStopOutAsync(long accountId, long instrumentId, DateOnly date, CancellationToken ct);
    Task<DailyAccountPnl> GetOrCreateDayPnlAsync(long accountId, DateOnly date, TradeMode mode, decimal openingBalance, decimal availableMargin, CancellationToken ct);
    Task<DailyAccountPnl?> DayPnlAsync(long accountId, DateOnly date, CancellationToken ct);
    Task UpdateDayPnlAsync(DailyAccountPnl pnl, CancellationToken ct);
    Task<List<DailyAccountPnl>> DayPnlsAsync(DateOnly date, CancellationToken ct);
    Task<DailyBotState> GetOrCreateBotStateAsync(DateOnly date, CancellationToken ct);
    Task UpdateBotStateAsync(DailyBotState state, CancellationToken ct);
    Task AddEventAsync(SystemEvent e, CancellationToken ct);
    Task<List<SystemEvent>> EventsAsync(int limit, CancellationToken ct);
    /// <summary>Events with id greater than <paramref name="afterId"/>, oldest first.</summary>
    Task<List<SystemEvent>> EventsAfterAsync(long afterId, int limit, CancellationToken ct);
}

/// <summary>Daily Paytm scrip-master download into mst.security_master.</summary>
public interface IScripMasterSync
{
    Task<int> SyncAsync(DateOnly today, CancellationToken ct);
}
