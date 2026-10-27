using TradeBot.Domain.Common;

namespace TradeBot.Domain.Entities.Mst;

/// <summary>The server's VAPID key pair for Web Push (single row, created on first use).</summary>
public class WebPushKeys : AuditableEntity
{
    public const int SingletonId = 1;

    public int WebPushKeysId { get; set; } = SingletonId;
    /// <summary>Uncompressed P-256 point, base64url (what browsers take as applicationServerKey).</summary>
    public required string PublicKey { get; set; }
    /// <summary>P-256 private scalar, base64url.</summary>
    [Encrypted] public required string PrivateKey { get; set; }
}

/// <summary>A browser or installed app that receives alerts as system notifications (one row per push endpoint).</summary>
public class WebPushSubscription : AuditableEntity
{
    public long WebPushSubscriptionId { get; set; }
    /// <summary>Push-service URL issued to the browser; unique per device and browser profile.</summary>
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    [Encrypted] public required string Auth { get; set; }
    public required string DeviceLabel { get; set; }
    public bool NotifyOnEntry { get; set; }
    public bool NotifyOnExit { get; set; }
    public bool NotifyOnCritical { get; set; } = true;
    public bool NotifyOnInfo { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
}
