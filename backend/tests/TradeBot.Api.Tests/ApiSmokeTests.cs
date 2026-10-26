using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TradeBot.Api.Tests;

[Collection(ApiCollection.Name)]
public class ApiSmokeTests(ApiFactory api)
{
    [Fact]
    public async Task Health_is_anonymous()
    {
        var r = await api.CreateClient().GetFromJsonAsync<JsonObject>("/health");
        Assert.Equal("ok", r!["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/api/dashboard/overview")]
    [InlineData("/api/accounts")]
    [InlineData("/api/validation/rules")]
    [InlineData("/api/trades")]
    public async Task Protected_endpoints_require_a_token(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(path)).StatusCode);

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var r = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("AUTH_INVALID", (await r.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rules_document_exposes_rules_forms_and_fixed_limits()
    {
        var client = await api.AuthorizedClientAsync();
        var doc = (await client.GetFromJsonAsync<JsonObject>("/api/validation/rules"))!;
        Assert.True(doc["rules"]!.AsArray().Count >= 50);
        var risk = doc["forms"]!.AsArray().Single(f => f!["form"]!.GetValue<string>() == TradeBot.Application.Validation.Forms.FormRuleCatalog.RiskConfig)!;
        Assert.Contains(risk["fields"]!.AsArray(), f => f!["field"]!.GetValue<string>() == "maxDailyLoss");
        Assert.Equal("09:15:00", doc["fixedLimits"]!["marketOpen"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_account_is_rejected_with_field_errors()
    {
        var client = await api.AuthorizedClientAsync();
        var r = await client.PostAsJsonAsync("/api/accounts", new { accountName = "", userName = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var fields = (await r.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray().Select(e => e!["field"]?.GetValue<string>()).ToList();
        Assert.Contains("accountName", fields);
        Assert.Contains("clientId", fields);
    }

    [Fact]
    public async Task Unknown_resources_return_404()
    {
        var client = await api.AuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/accounts/987654")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/trades/987654/exit", null)).StatusCode);
    }

    [Fact]
    public async Task Strategy_config_round_trips_and_validates()
    {
        var client = await api.AuthorizedClientAsync();
        var cfg = (await client.GetFromJsonAsync<JsonObject>("/api/strategy-config"))!;
        Assert.Equal("14:45:00", cfg["entryCutoffTime"]!.GetValue<string>());

        var bad = await client.PutAsJsonAsync("/api/strategy-config", new { squareOffTime = "15:45:00" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Dashboard_socket_pushes_the_overview()
    {
        var client = await api.AuthorizedClientAsync();
        var token = client.DefaultRequestHeaders.Authorization!.Parameter;
        var ws = api.Server.CreateWebSocketClient();
        using var socket = await ws.ConnectAsync(new Uri(api.Server.BaseAddress, $"/ws/dashboard?access_token={token}"), CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var msg = JsonNode.Parse(await ReceiveAsync(socket, timeout.Token))!;
        Assert.Equal("overview", msg["type"]!.GetValue<string>());
        Assert.NotNull(msg["data"]!["bot"]);

        // New system events are pushed for toasts
        await using (var db = await api.DbAsync())
        {
            db.Add(new TradeBot.Domain.Entities.Trn.SystemEvent { EventType = "SOCKET_TEST", Severity = TradeBot.Domain.Enums.EventSeverity.Critical, Message = "ping" });
            await db.SaveChangesAsync();
        }
        JsonNode? events;
        do events = JsonNode.Parse(await ReceiveAsync(socket, timeout.Token));
        while (events!["type"]!.GetValue<string>() != "events");
        var ev = events["data"]!.AsArray().Single()!;
        Assert.Equal(("SOCKET_TEST", "CRITICAL"), (ev["eventType"]!.GetValue<string>(), ev["severity"]!.GetValue<string>()));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var sb = new StringBuilder();
        WebSocketReceiveResult r;
        do
        {
            r = await socket.ReceiveAsync(buffer, ct);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        } while (!r.EndOfMessage);
        return sb.ToString();
    }
}
