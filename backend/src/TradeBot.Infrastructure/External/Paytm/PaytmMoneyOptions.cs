namespace TradeBot.Infrastructure.External.Paytm;

/// <summary>Bound from "PaytmMoney" in appsettings. Endpoints follow the official pyPMClient SDK routes.</summary>
public sealed class PaytmMoneyOptions
{
    public string BaseUrl { get; set; } = "https://developer.paytmmoney.com";
    public string LoginUrl { get; set; } = "https://login.paytmmoney.com/merchant-login";
    public string WebSocketUrl { get; set; } = "wss://developer-ws.paytmmoney.com/broadcast/user/v1/data";
    /// <summary>Order "source" field; verify against your API app configuration.</summary>
    public string OrderSource { get; set; } = "N";
    public string SecurityMasterFile { get; set; } = "security_master.csv";
    public int MaxSubscriptionsPerConnection { get; set; } = 200;
    /// <summary>Strike values in the scrip master / option chain are divided by this (set 100 if they arrive in paise).</summary>
    public decimal StrikeDivisor { get; set; } = 1;
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Our timeframe codes → price-chart "interval" values.</summary>
    public Dictionary<string, string> PriceChartIntervals { get; set; } = new()
    {
        ["1m"] = "minute", ["3m"] = "3minute", ["5m"] = "5minute", ["15m"] = "15minute", ["1h"] = "60minute", ["1d"] = "day",
    };
}
