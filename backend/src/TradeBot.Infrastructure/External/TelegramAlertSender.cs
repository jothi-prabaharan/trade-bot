using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.External;

/// <summary>Sends alerts to every active Telegram channel in mst.alert_channels that subscribes to the kind.</summary>
public sealed class TelegramAlertSender(HttpClient http, IDbContextFactory<TradeBotDbContext> dbFactory, ILogger<TelegramAlertSender> log) : IAlertSender
{
    public async Task SendAsync(AlertKind kind, string html, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channels = await db.AlertChannels.AsNoTracking()
            .Where(c => c.IsActive && c.ChannelType == AlertChannelType.Telegram)
            .ToListAsync(ct);

        foreach (var c in channels.Where(c => kind switch
                 {
                     AlertKind.Entry => c.NotifyOnEntry,
                     AlertKind.Exit => c.NotifyOnExit,
                     AlertKind.Critical => c.NotifyOnCritical,
                     _ => true,
                 }))
        {
            try
            {
                using var res = await http.PostAsJsonAsync($"https://api.telegram.org/bot{c.BotToken}/sendMessage",
                    new { chat_id = c.ChatId, text = html, parse_mode = "HTML", disable_web_page_preview = true }, ct);
                if (!res.IsSuccessStatusCode)
                    log.LogWarning("Telegram channel {Channel} returned {Status}", c.ChannelName, (int)res.StatusCode);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning(e, "Telegram alert to {Channel} failed", c.ChannelName);
            }
        }
    }
}
