using System.Net;
using TradeBot.Application.Validation;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

/// <summary>Telegram HTML message templates.</summary>
public static class Alerts
{
    public static string Entry(LiveTrade t) =>
        $"""
        🟢 <b>ENTRY</b> {Mode(t)} · {E(t.Account.AccountName)}
        <b>{E(t.Symbol ?? t.Trade.SecurityId.ToString())}</b> × {t.Trade.Quantity} ({t.Trade.Lots} lot{(t.Trade.Lots == 1 ? "" : "s")})
        Entry ₹{t.Trade.EntryBuyPrice:0.00} · SL ₹{t.Trade.StopLossPrice:0.00} · Target ₹{t.Trade.TargetSellPrice:0.00}
        Pattern: {E(t.PatternName ?? "—")}
        News: {E(t.NewsHeadline ?? "—")}
        """;

    public static string Exit(LiveTrade t, decimal net) =>
        $"""
        {(net >= 0 ? "✅" : "🔻")} <b>EXIT</b> {Mode(t)} · {E(t.Account.AccountName)} · {DbValue.ToDb(t.Trade.TradeStatus)}
        <b>{E(t.Symbol ?? t.Trade.SecurityId.ToString())}</b> × {t.Trade.Quantity}
        Entry ₹{t.Trade.EntryBuyPrice:0.00} → Exit ₹{t.Trade.ExitSellPrice:0.00}
        Gross ₹{t.Trade.GrossPnl:0.00} · Charges ₹{t.Trade.BrokerageAndCharges:0.00} · <b>Net ₹{net:0.00}</b>
        """;

    private static string Mode(LiveTrade t) => t.Trade.TradeMode == TradeMode.Real ? "🔴 REAL" : "📝 PAPER";
    private static string E(string s) => WebUtility.HtmlEncode(s);
}
