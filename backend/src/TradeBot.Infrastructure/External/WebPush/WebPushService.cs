using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.External.WebPush;

/// <summary>
/// Web Push (RFC 8030/8291/8292) to the dashboard's service worker. Subscriptions and the VAPID key pair live in
/// mst.*, so the API, News and Auth hosts can all send. Payloads use the Angular service-worker notification format.
/// </summary>
public sealed class WebPushService(
    PushServiceClient client, IDbContextFactory<TradeBotDbContext> dbFactory, TimeProvider clock, string subject, ILogger<WebPushService> log) : IWebPushService
{
    public const int MaxLabelLength = 100;

    public async Task<string> PublicKeyAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.WebPushKeys.AsNoTracking().SingleOrDefaultAsync(ct) is { } keys) return keys.PublicKey;

        var (publicKey, privateKey) = VapidKeys.Generate();
        db.WebPushKeys.Add(new WebPushKeys { PublicKey = publicKey, PrivateKey = privateKey });
        try
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Created the Web Push VAPID key pair");
            return publicKey;
        }
        catch (DbUpdateException)
        {
            // Another request created it first
            await using var again = await dbFactory.CreateDbContextAsync(ct);
            return (await again.WebPushKeys.AsNoTracking().SingleAsync(ct)).PublicKey;
        }
    }

    public async Task<PushDevice> SubscribeAsync(PushSubscribeRequest r, CancellationToken ct)
    {
        Validate(r);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.WebPushSubscriptions.SingleOrDefaultAsync(x => x.Endpoint == r.Endpoint, ct);
        if (s is null)
        {
            s = new WebPushSubscription { Endpoint = r.Endpoint, P256dh = r.P256dh, Auth = r.Auth, DeviceLabel = "" };
            db.WebPushSubscriptions.Add(s);
        }
        s.P256dh = r.P256dh;
        s.Auth = r.Auth;
        s.DeviceLabel = Label(r.DeviceLabel);
        Apply(s, r.Kinds);
        s.FailureCount = 0;
        s.LastError = null;
        await db.SaveChangesAsync(ct);
        return ToDevice(s);
    }

    public async Task<IReadOnlyList<PushDevice>> DevicesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.WebPushSubscriptions.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct)).Select(ToDevice).ToList();
    }

    public async Task<PushDevice?> UpdateKindsAsync(long id, PushAlertKinds kinds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.WebPushSubscriptions.FindAsync([id], ct) is not { } s) return null;
        Apply(s, kinds);
        await db.SaveChangesAsync(ct);
        return ToDevice(s);
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WebPushSubscriptions.Where(s => s.WebPushSubscriptionId == id).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<PushDeliveryResult> SendAsync(AlertKind kind, string title, string body, long? deviceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.WebPushSubscriptions.AsQueryable();
        q = deviceId is { } id ? q.Where(s => s.WebPushSubscriptionId == id) : kind switch
        {
            AlertKind.Entry => q.Where(s => s.NotifyOnEntry),
            AlertKind.Exit => q.Where(s => s.NotifyOnExit),
            AlertKind.Critical => q.Where(s => s.NotifyOnCritical),
            _ => q.Where(s => s.NotifyOnInfo),
        };
        var targets = await q.ToListAsync(ct);
        if (targets.Count == 0 || await db.WebPushKeys.AsNoTracking().SingleOrDefaultAsync(ct) is not { } keys) return new(0, 0, 0);

        using var vapid = new VapidAuthentication(keys.PublicKey, keys.PrivateKey) { Subject = subject };
        var message = PushPayload.Json(kind, title, body, clock.GetUtcNow());
        int sent = 0, failed = 0, removed = 0;
        // One at a time: VapidAuthentication signs with a shared ECDsa instance
        foreach (var s in targets)
        {
            var subscription = new PushSubscription { Endpoint = s.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, s.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, s.Auth);
            var push = new PushMessage(message)
            {
                Urgency = kind == AlertKind.Critical ? PushMessageUrgency.High : PushMessageUrgency.Normal,
                // A trade alert is stale after a quarter of an hour; keep critical and daily ones for an hour
                TimeToLive = kind is AlertKind.Entry or AlertKind.Exit ? 900 : 3600,
            };
            try
            {
                await client.RequestPushMessageDeliveryAsync(subscription, push, vapid, VapidAuthenticationScheme.Vapid, ct);
                s.LastSuccessAt = clock.GetUtcNow().UtcDateTime;
                s.FailureCount = 0;
                s.LastError = null;
                sent++;
            }
            catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // The browser unsubscribed or the subscription expired: it will never work again
                db.WebPushSubscriptions.Remove(s);
                removed++;
                log.LogInformation("Removed expired push subscription {Device}", s.DeviceLabel);
            }
            catch (Exception e) when (e is PushServiceClientException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                s.FailureCount++;
                s.LastError = Clip(e is PushServiceClientException p ? $"{(int)p.StatusCode} {p.Message}" : e.Message, 500);
                failed++;
                log.LogWarning("Push to {Device} failed: {Error}", s.DeviceLabel, s.LastError);
            }
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return new(sent, failed, removed);
    }

    /// <summary>VAPID subject: WebPush:Subject, else the dashboard origin. Push services need an https: or mailto: contact.</summary>
    public static string ResolveSubject(IConfiguration config)
    {
        var s = config["WebPush:Subject"] ?? config["Cors:Origins:0"];
        return s is not null && (s.StartsWith("https://", StringComparison.Ordinal) || s.StartsWith("mailto:", StringComparison.Ordinal))
            ? s : "mailto:tradebot@localhost";
    }

    private static void Validate(PushSubscribeRequest r)
    {
        if (!Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || r.Endpoint.Length > 1000)
            throw new ArgumentException("The push endpoint must be an https URL of at most 1000 characters.");
        if (Decode(r.P256dh) is not { Length: 65 } point || point[0] != 0x04)
            throw new ArgumentException("p256dh must be an uncompressed P-256 public key (base64url).");
        if (Decode(r.Auth) is not { Length: 16 })
            throw new ArgumentException("auth must be a 16-byte secret (base64url).");
    }

    private static byte[]? Decode(string? s)
    {
        try { return string.IsNullOrEmpty(s) ? null : Base64Url.DecodeFromChars(s.TrimEnd('=')); }
        catch (FormatException) { return null; }
    }

    private static string Label(string? s) => string.IsNullOrWhiteSpace(s) ? "Browser" : Clip(s.Trim(), MaxLabelLength);
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static void Apply(WebPushSubscription s, PushAlertKinds k)
    {
        s.NotifyOnEntry = k.Entry;
        s.NotifyOnExit = k.Exit;
        s.NotifyOnCritical = k.Critical;
        s.NotifyOnInfo = k.Info;
    }

    private static PushDevice ToDevice(WebPushSubscription s) =>
        new(s.WebPushSubscriptionId, s.Endpoint, s.DeviceLabel, new(s.NotifyOnEntry, s.NotifyOnExit, s.NotifyOnCritical, s.NotifyOnInfo),
            s.CreatedAt, s.LastSuccessAt, s.FailureCount, s.LastError);
}

public static class VapidKeys
{
    /// <summary>New P-256 key pair as base64url (public: 65-byte uncompressed point, private: 32-byte scalar).</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(includePrivateParameters: true);
        return (Base64Url.EncodeToString([0x04, .. p.Q.X!, .. p.Q.Y!]), Base64Url.EncodeToString(p.D!));
    }
}

/// <summary>Notification JSON in the shape the Angular service worker shows (and focuses the app on click).</summary>
public static class PushPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Json(AlertKind kind, string title, string body, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            notification = new
            {
                title,
                body,
                icon = "/icons/icon-192.png",
                badge = "/icons/badge-96.png",
                // Critical alerts stay on screen until dismissed
                requireInteraction = kind == AlertKind.Critical ? true : (bool?)null,
                vibrate = kind == AlertKind.Critical ? new[] { 200, 100, 200 } : null,
                timestamp = now.ToUnixTimeMilliseconds(),
                data = new { kind = kind.ToString().ToUpperInvariant(), onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url = "/" } } },
            },
        }, Options);
}

/// <summary>Turns a Telegram HTML alert into a notification title and plain-text body.</summary>
public static partial class PushText
{
    // Encrypted push payloads are capped near 4 KB; keep well under it
    private const int MaxBody = 1500;

    public static (string Title, string Body) FromAlert(AlertKind kind, string html)
    {
        var lines = WebUtility.HtmlDecode(Tags().Replace(html, "")).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var fallback = kind switch
        {
            AlertKind.Critical => "TradeBot alert",
            AlertKind.Entry => "Trade opened",
            AlertKind.Exit => "Trade closed",
            _ => "TradeBot",
        };
        return lines.Count switch
        {
            0 => (fallback, ""),
            1 => (fallback, Clip(lines[0], MaxBody)),
            _ => (Clip(lines[0], 120), Clip(string.Join('\n', lines.Skip(1)), MaxBody)),
        };
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}

/// <summary>Alert channel for the dispatcher: every subscribed device that wants this kind of alert.</summary>
public sealed class WebPushAlertSender(IWebPushService push) : IAlertSender
{
    public async Task SendAsync(AlertKind kind, string html, CancellationToken ct)
    {
        var (title, body) = PushText.FromAlert(kind, html);
        await push.SendAsync(kind, title, body, deviceId: null, ct);
    }
}
