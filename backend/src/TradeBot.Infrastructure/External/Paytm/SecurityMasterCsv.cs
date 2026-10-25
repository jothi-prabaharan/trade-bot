using System.Globalization;
using System.Text;
using TradeBot.Domain.Enums;

namespace TradeBot.Infrastructure.External.Paytm;

public sealed record ScripRow(
    long SecurityId, string Exchange, string Segment, string Underlying, string Symbol, SecurityType Type,
    decimal? Strike, OptionType? OptType, DateOnly? Expiry, int LotSize, decimal TickSize, int? FreezeQuantity);

/// <summary>
/// Parses the Paytm scrip-master CSV by header name (aliases cover known variants), so column order changes
/// don't break the daily sync. Rows of unknown instrument types are skipped.
/// </summary>
public static class SecurityMasterCsv
{
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["id"] = ["security_id", "securityid", "scrip_id", "token"],
        ["exchange"] = ["exchange", "exch", "exchange_segment"],
        ["segment"] = ["segment", "seg"],
        ["underlying"] = ["name", "underlying", "underlying_symbol", "symbol_name"],
        ["symbol"] = ["pml_symbol", "trading_symbol", "symbol", "scrip_name"],
        ["type"] = ["instrument_type", "instrument", "inst_type", "instrumenttype"],
        ["strike"] = ["strike_price", "strike", "strikeprice"],
        ["opt"] = ["option_type", "opt_type", "optiontype"],
        ["expiry"] = ["expiry_date", "expiry", "expirydate"],
        ["lot"] = ["lot_size", "lotsize", "market_lot", "lot"],
        ["tick"] = ["tick_size", "ticksize", "tick"],
        ["freeze"] = ["freeze_quantity", "freeze_qty", "freezeqty"],
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd-MM-yyyy", "dd-MMM-yyyy", "ddMMMyyyy", "yyyyMMdd", "dd/MM/yyyy", "yyyy-MM-dd HH:mm:ss"];

    public static IEnumerable<ScripRow> Parse(string csv, decimal strikeDivisor = 1)
    {
        using var reader = new StringReader(csv);
        var header = reader.ReadLine();
        if (header is null) yield break;
        var cols = SplitLine(header).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var idx = Aliases.ToDictionary(a => a.Key, a => a.Value.Select(name => cols.IndexOf(name)).FirstOrDefault(i => i >= 0, -1));
        if (idx["id"] < 0 || idx["type"] < 0)
            throw new FormatException($"Scrip master header missing security id / instrument type columns: {header}");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = SplitLine(line);
            string Get(string key) => idx[key] >= 0 && idx[key] < f.Count ? f[idx[key]].Trim() : "";

            if (!long.TryParse(Get("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            if (MapType(Get("type")) is not { } type) continue;

            var isDerivative = type is SecurityType.FutIdx or SecurityType.FutStk or SecurityType.OptIdx or SecurityType.OptStk;
            var tick = Dec(Get("tick")) ?? 0.05m;
            if (isDerivative && tick >= 1) tick /= 100; // ticks quoted in paise
            var strike = Dec(Get("strike")) is { } s and > 0 ? s / strikeDivisor : (decimal?)null;
            var opt = Get("opt").ToUpperInvariant() switch { "CE" => OptionType.Ce, "PE" => OptionType.Pe, _ => (OptionType?)null };
            var isOption = type is SecurityType.OptIdx or SecurityType.OptStk;
            if (isOption && (strike is null || opt is null)) continue;

            yield return new ScripRow(
                id,
                Get("exchange") is { Length: > 0 } ex ? ex.ToUpperInvariant() : "NSE",
                Get("segment") is { Length: > 0 } seg ? seg.ToUpperInvariant()[..1] : isDerivative ? "D" : "E",
                (Get("underlying") is { Length: > 0 } u ? u : Get("symbol")).ToUpperInvariant(),
                Get("symbol") is { Length: > 0 } sym ? sym : Get("underlying"),
                type,
                isOption ? strike : null,
                isOption ? opt : null,
                ParseDate(Get("expiry")),
                int.TryParse(Get("lot"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lot) && lot > 0 ? lot : 1,
                tick,
                int.TryParse(Get("freeze"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fq) && fq > 0 ? fq : null);
        }
    }

    public static SecurityType? MapType(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "OPTIDX" => SecurityType.OptIdx,
        "OPTSTK" => SecurityType.OptStk,
        "FUTIDX" => SecurityType.FutIdx,
        "FUTSTK" => SecurityType.FutStk,
        "INDEX" or "IDX" => SecurityType.Index,
        "EQUITY" or "EQ" or "ES" => SecurityType.Equity,
        _ => null,
    };

    private static decimal? Dec(string s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateOnly? ParseDate(string s) =>
        s.Length > 0 && DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? DateOnly.FromDateTime(d) : null;

    /// <summary>RFC 4180 field split (quoted fields may contain commas and doubled quotes).</summary>
    internal static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
