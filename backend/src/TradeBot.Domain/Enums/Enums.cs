namespace TradeBot.Domain.Enums;

public enum TradeMode { Paper, Real }

public enum InstrumentType { Index, Stock }

public enum Exchange { Nse, Bse }

public enum Segment
{
    [DbValue("D")] Derivatives,
    [DbValue("E")] Equity
}

public enum SecurityType
{
    [DbValue("INDEX")] Index,
    [DbValue("EQUITY")] Equity,
    [DbValue("FUTIDX")] FutIdx,
    [DbValue("FUTSTK")] FutStk,
    [DbValue("OPTIDX")] OptIdx,
    [DbValue("OPTSTK")] OptStk
}

public enum OptionType { Ce, Pe }

public enum TxnType
{
    [DbValue("B")] Buy,
    [DbValue("S")] Sell
}

public enum ProductType
{
    [DbValue("I")] Intraday,
    [DbValue("M")] Margin,
    [DbValue("B")] Bracket
}

public enum OrderType
{
    [DbValue("LMT")] Limit,
    [DbValue("SL")] StopLossLimit
}

public enum OrderLegType { Entry, StopLoss, TargetSell, EmergencyExit }

public enum OrderStatus { Pending, Placed, Open, TriggerPending, PartiallyFilled, Filled, Cancelled, Rejected, Failed }

public enum TradeStatus { Open, TargetHit, SlHit, TrailingSlHit, TimeSquareOff, KillSwitchExit, ManualExit }

public enum SignalType { Buy, Sell }

public enum SignalStatus { New, Dispatched, Rejected, Expired }

public enum SlMethod { Structural, FixedPctFallback }

public enum PatternCategory { Candlestick, Classical, Harmonic, SmcStructure }

public enum PatternDirection { Bullish, Bearish, Neutral, Bidirectional }

public enum MarketBias { Bullish, Bearish, Neutral }

public enum ImpactLevel { Low, Medium, High, ExtremeHalt }

public enum NewsCategory { Earnings, CorporateAction, Regulatory, MergerAcquisition, Management, OrderWin, Rating, Legal, Sector, Macro, Global, Other }

public enum NewsScanType { Premarket, IntradaySpike, Manual, Periodic }

public enum Timeframe
{
    [DbValue("1m")] M1,
    [DbValue("3m")] M3,
    [DbValue("5m")] M5,
    [DbValue("15m")] M15,
    [DbValue("1h")] H1,
    [DbValue("1d")] D1
}

public enum LoginStatus { Pending, Success, Failed, Expired }

public enum LoginMethod { Headless, Manual }

public enum StrikeSelection { Atm, Itm1, Otm1, DeltaTarget }

public enum ExpiryPolicy { NearestSkipExpiryDay, Nearest, MonthlyOnly }

public enum AlertChannelType { Telegram, Email }

public enum EventSeverity { Info, Warning, Critical }

public enum WatchlistSource { Index, Premarket, VolumeSpike, News }
