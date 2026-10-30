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

/// <summary>
/// A call Paytm answered with an error: HTTP status plus Paytm's own result code (e.g. PM_MERCHANT_AUTH_400607) and
/// message. <see cref="Body"/> is already redacted, so the exception can be logged or stored as is.
/// </summary>
public class BrokerApiException(string message, int statusCode, string? errorCode, string? brokerMessage, string body) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
    public string? BrokerMessage { get; } = brokerMessage;
    public string Body { get; } = body;

    /// <summary>Paytm refused the access token itself: only a fresh login fixes it.</summary>
    public bool TokenRejected => StatusCode is 401 or 403
        || string.Equals(ErrorCode, "PM_MERCHANT_AUTH_401669", StringComparison.OrdinalIgnoreCase)
        || BrokerMessage?.Contains("invalid token", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Paytm's clock minus ours, read from a response's Date header (whole seconds, so ±1 s at best).</summary>
public sealed record BrokerClockReading(TimeSpan Skew, DateTime MeasuredAtUtc);

/// <summary>Result of GET /accounts/v1/user/details with the account's access token.</summary>
public sealed record BrokerConnectionCheck(
    long AccountId, bool Ok, bool TokenRejected, DateTime CheckedAtUtc, string? ClientId, string? Name, string? Email, string? Phone,
    bool? ClientIdMatches, DateTime? TokenExpiryUtc, string? ErrorCode, string? Error, double? ClockSkewSeconds);

public interface IPaytmMoneyClient
{
    string LoginUrl(string apiKey, string state);
    /// <summary>Latest clock reading taken from any Paytm response; null until one arrives.</summary>
    BrokerClockReading? Clock { get; }
    /// <summary>Asks Paytm for its time (one light request) and returns the new reading, or null when unreachable.</summary>
    Task<BrokerClockReading?> ProbeClockAsync(CancellationToken ct);
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
    public const string OtpRequested = "tradebot_otp_requested";        // payload: OtpPrompt JSON (Auth worker → API)
    public const string OtpClosed = "tradebot_otp_closed";              // payload: account_id (Auth worker → API)
    public const string OtpSubmitted = "tradebot_otp_submitted";        // payload: account_id:otp (API → Auth worker)
}

public interface IBrokerLoginAutomation
{
    /// <summary>Drives the broker login page and returns the redirect's request token.</summary>
    Task<string> GetRequestTokenAsync(TradingAccount account, string loginUrl, CancellationToken ct);
}

/// <summary>An SMS OTP the login page is waiting for; the dashboard shows an entry box until <see cref="UntilUtc"/>.</summary>
public sealed record OtpPrompt(long AccountId, DateTime UntilUtc, string? SentTo, string? Note);

/// <summary>Asks the dashboard user for the SMS OTP Paytm just sent (the first login from a new browser profile needs one).</summary>
public interface ISmsOtpPrompt
{
    /// <summary>The OTP typed on the dashboard, or null when none arrived within <paramref name="timeout"/>.</summary>
    Task<string?> AskAsync(TradingAccount account, string? sentTo, string? note, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// A login failure that retrying cannot fix and would make worse: each new attempt can trigger another SMS OTP,
/// and repeated wrong passwords can lock the Paytm account.
/// </summary>
public sealed class BrokerLoginException(string message) : Exception(message);
