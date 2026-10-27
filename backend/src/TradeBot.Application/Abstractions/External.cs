using System.Threading.Channels;
using TradeBot.Application.Market;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Abstractions;

// ---------- Broker (Paytm Money Open API) ----------

public sealed record BrokerTokens(string AccessToken, string? PublicAccessToken, string? ReadAccessToken, DateTime? ExpiresUtc);

public sealed record BrokerProfile(string? ClientId, string? Name, string? Email, string? Phone, string Raw);

public sealed record BrokerFunds(decimal AvailableMargin, decimal UtilisedMargin, decimal? OpeningBalance, string Raw);

public sealed record BrokerOrderRequest(
    TxnType Side, long SecurityId, int Quantity, OrderType Type, decimal Price, decimal? TriggerPrice,
    ProductType Product = ProductType.Intraday, Segment Segment = Segment.Derivatives, string Exchange = "NSE");

/// <summary>Identifiers Paytm needs to modify or cancel an order (taken from the order book).</summary>
public sealed record BrokerOrderRef(string OrderNo, string? SerialNo = null, string? GroupId = null, string? MarketType = null);

public sealed record BrokerAck(bool Success, string? OrderNo, string? Message, string Raw);

public sealed record BrokerOrder(
    BrokerOrderRef Ref, long SecurityId, TxnType Side, int Quantity, int FilledQuantity, decimal? AveragePrice,
    OrderStatus Status, string? Message, string Raw);

public enum ScripType { Index, Equity, Future, Option, Etf }

public sealed record FeedSubscription(long SecurityId, ScripType Type, string Exchange = "NSE");

public sealed record PriceChartRequest(
    string Symbol, SecurityType InstType, Timeframe Interval, DateOnly From, DateOnly To,
    DateOnly? Expiry = null, decimal? Strike = null, OptionType? OptType = null, bool Continuous = true, string Exchange = "NSE");

public sealed record OptionChainRow(decimal Strike, OptionType Type, decimal? Ltp, long? OpenInterest, long? OiChange);

public interface IPaytmMoneyClient
{
    string LoginUrl(string apiKey, string state);
    Task<BrokerTokens> GenerateTokensAsync(string apiKey, string apiSecret, string requestToken, CancellationToken ct);
    Task<BrokerProfile> GetProfileAsync(string token, CancellationToken ct);
    Task<BrokerFunds> GetFundsAsync(string token, CancellationToken ct);
    Task<BrokerAck> PlaceOrderAsync(string accessToken, BrokerOrderRequest order, CancellationToken ct);
    Task<BrokerAck> ModifyOrderAsync(string accessToken, BrokerOrderRef reference, BrokerOrderRequest order, CancellationToken ct);
    Task<BrokerAck> CancelOrderAsync(string accessToken, BrokerOrderRef reference, BrokerOrderRequest order, CancellationToken ct);
    Task<IReadOnlyList<BrokerOrder>> GetOrdersAsync(string token, CancellationToken ct);
    Task<string> DownloadSecurityMasterAsync(string fileName, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetPriceChartAsync(string token, PriceChartRequest request, CancellationToken ct);
    Task<IReadOnlyList<OptionChainRow>> GetOptionChainAsync(string token, string symbol, OptionType type, DateOnly expiry, CancellationToken ct);
    Task<IReadOnlyList<Tick>> GetLiveQuotesAsync(string token, IReadOnlyCollection<FeedSubscription> scrips, CancellationToken ct);
}

/// <summary>Live tick stream from the data-provider account's WebSocket.</summary>
public interface IMarketFeed : IAsyncDisposable
{
    ChannelReader<Tick> Ticks { get; }
    bool IsConnected { get; }
    DateTime? LastTickUtc { get; }
    Task StartAsync(string publicAccessToken, CancellationToken ct);
    Task SubscribeAsync(IReadOnlyCollection<FeedSubscription> scrips, CancellationToken ct);
    Task UnsubscribeAsync(IReadOnlyCollection<FeedSubscription> scrips, CancellationToken ct);
}

// ---------- News intelligence (Gemini) ----------

public sealed record AiNewsItem(
    string? NseSymbol, string? IndexSymbol, string Headline, string? Summary, NewsCategory Category,
    MarketBias Sentiment, int SentimentScore, ImpactLevel Impact, DateTime? PublishedAtUtc, IReadOnlyList<string> SourceUrls);

public sealed record AiNewsResult(IReadOnlyList<AiNewsItem> Items, IReadOnlyList<string> GroundingUrls, string RawJson, bool UsedTwoStepFallback);

public interface INewsIntelligenceClient
{
    Task<AiNewsResult> ScanAsync(GeminiNewsConfig config, string apiKey, string prompt, CancellationToken ct);
}

// ---------- Alerts, events, login automation ----------

public enum AlertKind { Entry, Exit, Critical, Info }

public interface IAlertSender
{
    Task SendAsync(AlertKind kind, string html, CancellationToken ct);
}

// ---------- Web Push (installed dashboard / browser notifications) ----------

public sealed record PushAlertKinds(bool Entry, bool Exit, bool Critical, bool Info);

/// <summary>A browser's PushSubscription (endpoint + p256dh/auth keys) and which alerts it wants.</summary>
public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string DeviceLabel, PushAlertKinds Kinds);

public sealed record PushDevice(
    long Id, string Endpoint, string DeviceLabel, PushAlertKinds Kinds, DateTime CreatedAt, DateTime? LastSuccessAt, int FailureCount, string? LastError);

public sealed record PushDeliveryResult(int Sent, int Failed, int Removed);

public interface IWebPushService
{
    /// <summary>VAPID public key (base64url) the browser subscribes with; the key pair is created on first use.</summary>
    Task<string> PublicKeyAsync(CancellationToken ct);
    Task<PushDevice> SubscribeAsync(PushSubscribeRequest request, CancellationToken ct);
    Task<IReadOnlyList<PushDevice>> DevicesAsync(CancellationToken ct);
    Task<PushDevice?> UpdateKindsAsync(long id, PushAlertKinds kinds, CancellationToken ct);
    Task<bool> RemoveAsync(long id, CancellationToken ct);
    /// <summary>Sends to every device that wants <paramref name="kind"/> (or only <paramref name="deviceId"/>); expired subscriptions are removed.</summary>
    Task<PushDeliveryResult> SendAsync(AlertKind kind, string title, string body, long? deviceId, CancellationToken ct);
}

/// <summary>Cross-process notifications (PostgreSQL LISTEN/NOTIFY between API+Engine, News and Auth workers).</summary>
public interface IEventBus
{
    Task PublishAsync(string channel, string payload, CancellationToken ct);
    void Subscribe(string channel, Func<string, CancellationToken, Task> handler);
}

public static class EventChannels
{
    public const string TokensUpdated = "tradebot_tokens_updated";      // payload: account_id
    public const string NewsUpdated = "tradebot_news_updated";          // payload: instrument_id or "all"
    public const string NewsCheckRequested = "tradebot_news_check";     // payload: instrument_id (spike) or manual:{instrument_id|all}
    public const string ConfigChanged = "tradebot_config_changed";      // payload: table name
    public const string LoginRequested = "tradebot_login_requested";    // payload: account_id
}

public interface IBrokerLoginAutomation
{
    /// <summary>Drives the broker login page and returns the redirect's request token.</summary>
    Task<string> GetRequestTokenAsync(TradingAccount account, string loginUrl, CancellationToken ct);
}
