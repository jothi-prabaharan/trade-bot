using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeBot.Application.Abstractions;
using TradeBot.Infrastructure.External.WebPush;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Api.Tests;

public sealed record PushDelivery(string Endpoint, string? Authorization, string? Urgency, string? Ttl, string? ContentEncoding, byte[] Body);

/// <summary>Stands in for FCM / Mozilla / Apple: records each push request and answers 201 unless told otherwise.</summary>
public sealed class FakePushService : HttpMessageHandler
{
    public ConcurrentQueue<PushDelivery> Received { get; } = new();
    public ConcurrentDictionary<string, HttpStatusCode> Answer { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var endpoint = r.RequestUri!.ToString();
        Received.Enqueue(new(endpoint, r.Headers.Authorization?.ToString(), Header(r, "Urgency"), Header(r, "TTL"),
            r.Content?.Headers.ContentEncoding.FirstOrDefault(), await r.Content!.ReadAsByteArrayAsync(ct)));
        return new HttpResponseMessage(Answer.GetValueOrDefault(endpoint, HttpStatusCode.Created));
    }

    private static string? Header(HttpRequestMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.First() : null;
}

/// <summary>A browser's side of a push subscription: its ECDH key pair and auth secret, and RFC 8291 decryption.</summary>
public sealed class FakeBrowser : IDisposable
{
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _public;
    private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);

    public string Endpoint { get; } = $"https://push.example.test/send/{Guid.NewGuid():N}";

    public FakeBrowser()
    {
        var q = _ecdh.ExportParameters(false).Q;
        _public = [0x04, .. q.X!, .. q.Y!];
    }

    public object Subscription(string label, bool entry = false, bool exit = false, bool critical = true, bool info = false) => new
    {
        endpoint = Endpoint, p256dh = Base64Url.EncodeToString(_public), auth = Base64Url.EncodeToString(_auth), deviceLabel = label,
        kinds = new { entry, exit, critical, info },
    };

    /// <summary>RFC 8291 key derivation + RFC 8188 aes128gcm, single record.</summary>
    public JsonNode Decrypt(byte[] body)
    {
        var salt = body[..16];
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)) >= 18);
        var idLength = body[20];
        var serverPublic = body[21..(21 + idLength)];
        var cipher = body[(21 + idLength)..];

        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        var shared = _ecdh.DeriveRawSecretAgreement(server.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, _auth, [.. "WebPush: info"u8, 0, .. _public, .. serverPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, [.. "Content-Encoding: aes128gcm"u8, 0]);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, [.. "Content-Encoding: nonce"u8, 0]);

        var plain = new byte[cipher.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
        // Last record: content, 0x02 delimiter, zero padding
        var end = plain.Length - 1;
        while (plain[end] == 0) end--;
        Assert.Equal(0x02, plain[end]);
        return JsonNode.Parse(Encoding.UTF8.GetString(plain, 0, end))!;
    }

    public void Dispose() => _ecdh.Dispose();
}

[Collection(ApiCollection.Name)]
public class WebPushTests(ApiFactory api)
{
    private FakePushService Push => api.Push;

    private async Task<(HttpClient Client, string PublicKey)> SetUpAsync()
    {
        var client = await api.AuthorizedClientAsync();
        var config = (await client.GetFromJsonAsync<JsonObject>("/api/push/config"))!;
        return (client, (string)config["publicKey"]!);
    }

    private async Task<long> SubscribeAsync(HttpClient client, object subscription)
    {
        var r = await client.PostAsJsonAsync("/api/push/devices", subscription);
        r.EnsureSuccessStatusCode();
        return (long)(await r.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
    }

    private PushDelivery[] DeliveriesTo(string endpoint) => Push.Received.Where(d => d.Endpoint == endpoint).ToArray();

    [Fact]
    public async Task Test_notification_is_vapid_signed_encrypted_and_readable_by_the_browser()
    {
        var (client, publicKey) = await SetUpAsync();
        Assert.Equal(publicKey, (string)(await client.GetFromJsonAsync<JsonObject>("/api/push/config"))!["publicKey"]!);
        using var browser = new FakeBrowser();
        var id = await SubscribeAsync(client, browser.Subscription("Chrome on Android"));

        var result = (await (await client.PostAsync($"/api/push/devices/{id}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(1, (int)result["sent"]!);

        var d = Assert.Single(DeliveriesTo(browser.Endpoint));
        Assert.Equal("aes128gcm", d.ContentEncoding);
        // Normal is the protocol default, so the header may be left out
        Assert.True(d.Urgency is null or "normal", d.Urgency);
        Assert.Equal("3600", d.Ttl);

        // VAPID: an ES256 JWT for the push service's origin, verifiable with the key the browser subscribed with
        Assert.StartsWith("vapid ", d.Authorization);
        var parts = d.Authorization!["vapid ".Length..].Split(',', StringSplitOptions.TrimEntries).ToDictionary(p => p[..1], p => p[2..]);
        Assert.Equal(publicKey, parts["k"]);
        var jwt = parts["t"].Split('.');
        var point = Base64Url.DecodeFromChars(publicKey);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64Url.DecodeFromChars(jwt[2]), HashAlgorithmName.SHA256));
        var claims = JsonNode.Parse(Base64Url.DecodeFromChars(jwt[1]))!;
        Assert.Equal("https://push.example.test", (string?)claims["aud"]);
        Assert.Equal("mailto:ops@example.com", (string?)claims["sub"]);
        Assert.InRange(DateTimeOffset.FromUnixTimeSeconds((long)claims["exp"]!) - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromHours(24));

        var notification = browser.Decrypt(d.Body)["notification"]!;
        Assert.Equal("TradeBot test", (string?)notification["title"]);
        Assert.Equal("Notifications work on this device.", (string?)notification["body"]);

        var device = (await client.GetFromJsonAsync<JsonArray>("/api/push/devices"))!.Single(x => (long)x!["id"]! == id)!;
        Assert.NotNull(device["lastSuccessAt"]);
        Assert.Equal("Chrome on Android", (string?)device["deviceLabel"]);
    }

    [Fact]
    public async Task Alerts_reach_only_devices_that_want_that_kind()
    {
        var (client, _) = await SetUpAsync();
        using var critical = new FakeBrowser();
        using var trades = new FakeBrowser();
        await SubscribeAsync(client, critical.Subscription("Phone"));
        var tradesId = await SubscribeAsync(client, trades.Subscription("Laptop", entry: true, critical: false));

        var sender = new WebPushAlertSender(api.Get<IWebPushService>());
        await sender.SendAsync(AlertKind.Critical, "⚠️ Stop-loss order rejected for trade 41: <b>RMS</b>. Exiting the position.", CancellationToken.None);
        await sender.SendAsync(AlertKind.Entry, "🟢 <b>ENTRY</b> 📝 PAPER · Paper Alpha\n<b>NIFTY 25000 CE</b> × 75", CancellationToken.None);

        var c = Assert.Single(DeliveriesTo(critical.Endpoint));
        Assert.Equal("high", c.Urgency);
        var n = critical.Decrypt(c.Body)["notification"]!;
        Assert.Equal("TradeBot alert", (string?)n["title"]);
        Assert.Equal("⚠️ Stop-loss order rejected for trade 41: RMS. Exiting the position.", (string?)n["body"]);
        Assert.True((bool?)n["requireInteraction"]);

        var e = Assert.Single(DeliveriesTo(trades.Endpoint));
        Assert.Equal("900", e.Ttl);
        Assert.Equal("🟢 ENTRY 📝 PAPER · Paper Alpha", (string?)trades.Decrypt(e.Body)["notification"]!["title"]);

        // Changing the device's choices takes effect on the next alert
        (await client.PutAsJsonAsync($"/api/push/devices/{tradesId}/kinds", new { entry = false, exit = false, critical = true, info = false })).EnsureSuccessStatusCode();
        await sender.SendAsync(AlertKind.Entry, "🟢 ENTRY again", CancellationToken.None);
        Assert.Single(DeliveriesTo(trades.Endpoint));
    }

    [Fact]
    public async Task Expired_subscriptions_are_removed_and_failures_are_recorded()
    {
        var (client, _) = await SetUpAsync();
        using var gone = new FakeBrowser();
        using var flaky = new FakeBrowser();
        var goneId = await SubscribeAsync(client, gone.Subscription("Old tablet"));
        var flakyId = await SubscribeAsync(client, flaky.Subscription("Desktop"));
        Push.Answer[gone.Endpoint] = HttpStatusCode.Gone;
        Push.Answer[flaky.Endpoint] = HttpStatusCode.InternalServerError;

        Assert.Equal(new PushDeliveryResult(0, 0, 1), await api.Get<IWebPushService>().SendAsync(AlertKind.Critical, "t", "b", goneId, CancellationToken.None));
        Assert.Equal(new PushDeliveryResult(0, 1, 0), await api.Get<IWebPushService>().SendAsync(AlertKind.Critical, "t", "b", flakyId, CancellationToken.None));

        var devices = (await client.GetFromJsonAsync<JsonArray>("/api/push/devices"))!;
        Assert.DoesNotContain(devices, x => (long)x!["id"]! == goneId);
        var failing = devices.Single(x => (long)x!["id"]! == flakyId)!;
        Assert.Equal(1, (int)failing["failureCount"]!);
        Assert.StartsWith("500", (string?)failing["lastError"]);

        // Re-subscribing the same endpoint updates it in place and clears the failure
        Assert.Equal(flakyId, await SubscribeAsync(client, flaky.Subscription("Desktop (Edge)")));
        var again = (await client.GetFromJsonAsync<JsonArray>("/api/push/devices"))!.Single(x => (long)x!["id"]! == flakyId)!;
        Assert.Equal(0, (int)again["failureCount"]!);
        Assert.Equal("Desktop (Edge)", (string?)again["deviceLabel"]);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/push/devices/{flakyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/push/devices/{flakyId}/test", null)).StatusCode);
    }

    [Fact]
    public async Task Bad_subscriptions_are_rejected_and_secrets_are_encrypted_at_rest()
    {
        var (client, _) = await SetUpAsync();
        using var browser = new FakeBrowser();
        var good = JsonSerializer.SerializeToNode(browser.Subscription("Phone"))!.AsObject();

        foreach (var (field, value) in new[] { ("endpoint", "http://push.example.test/x"), ("p256dh", "AAAA"), ("auth", "not*base64") })
        {
            var bad = good.DeepClone().AsObject();
            bad[field] = value;
            var r = await client.PostAsJsonAsync("/api/push/devices", bad);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("PUSH_INVALID_SUBSCRIPTION", (string?)(await r.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["code"]);
        }

        await SubscribeAsync(client, good);
        await using var db = await api.DbAsync();
        var keys = await db.WebPushKeys.AsNoTracking().SingleAsync();
        var sub = await db.WebPushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == browser.Endpoint);
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT k.private_key, s.auth FROM mst.web_push_keys k, mst.web_push_subscriptions s WHERE s.endpoint = @e", conn);
        cmd.Parameters.AddWithValue("e", browser.Endpoint);
        await using var row = await cmd.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.NotEqual(keys.PrivateKey, row.GetString(0));
        Assert.NotEqual(sub.Auth, row.GetString(1));
    }
}
