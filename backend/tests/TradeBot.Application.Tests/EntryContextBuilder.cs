using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Tests;

/// <summary>A fully valid PAPER entry on Tue 27-Oct-2026 10:30 IST; tests mutate one thing at a time.</summary>
internal static class EntryContextBuilder
{
    public static readonly DateTime NowUtc = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);
    public static readonly DateOnly TodayIst = new(2026, 10, 27);

    public static EntryContext Valid(Action<Parts>? mutate = null)
    {
        var p = new Parts();
        mutate?.Invoke(p);
        return new EntryContext
        {
            NowUtc = p.NowUtc,
            Calendar = new MarketCalendar(p.Holidays),
            Account = p.Account,
            Risk = p.Risk,
            LatestToken = p.Token,
            DayPnl = p.DayPnl,
            BotState = p.BotState,
            OpenTradeCount = p.OpenTradeCount,
            HasOrderForSignal = p.HasOrderForSignal,
            Strategy = p.Strategy,
            Gemini = p.Gemini,
            Instrument = p.Instrument,
            Security = p.Security,
            Pattern = p.Pattern,
            Detection = p.Detection,
            Signal = p.Signal,
            Sentiment = p.Sentiment,
            TodaysNews = p.News,
            Quote = p.Quote,
            BrokeragePerOrder = 20,
        };
    }

    internal sealed class Parts
    {
        public DateTime NowUtc = EntryContextBuilder.NowUtc;
        public List<MarketHoliday> Holidays = [];
        public TradingAccount Account = new() { AccountId = 1, AccountName = "Paper A", ClientId = "PAPER-A", UserName = "u" };
        public AccountRiskConfig Risk = new() { AccountId = 1 };
        public AccountAuthToken? Token;
        public DailyAccountPnl? DayPnl = new() { AccountId = 1, TradingDate = TodayIst };
        public DailyBotState? BotState = new() { TradingDate = TodayIst };
        public int OpenTradeCount;
        public bool HasOrderForSignal;
        public StrategyConfig Strategy = new();
        public GeminiNewsConfig Gemini = new() { ModelName = "gemini-flash-latest" };
        public Instrument Instrument = new() { InstrumentId = 10, Symbol = "NIFTY", InstrumentType = InstrumentType.Index, LotSize = 65, FreezeQuantity = 1800, TickSize = 0.05m, IsTradable = true };
        public SecurityMaster Security = new()
        {
            SecurityId = 500, InstrumentId = 10, PmlSymbol = "NIFTY 25000 CE", SecurityType = SecurityType.OptIdx, StrikePrice = 25000,
            OptType = OptionType.Ce, ExpiryDate = TodayIst.AddDays(7), LotSize = 65, TickSize = 0.05m, SyncedOn = TodayIst
        };
        public ChartPattern Pattern = new() { PatternId = 3, PatternCode = "BULLISH_ENGULFING", PatternName = "Bullish Engulfing", Category = PatternCategory.Candlestick, Direction = PatternDirection.Bullish };
        public DetectedPattern Detection = new() { DetectionId = 7, InstrumentId = 10, PatternId = 3, Direction = PatternDirection.Bullish, ConfidenceScore = 78, IsConfirmed = true };
        public Signal Signal = new()
        {
            SignalId = 9, DetectionId = 7, InstrumentId = 10, SecurityId = 500, SignalType = SignalType.Buy,
            EntryOptionLtp = 100, MandatorySlPrice = 85, MandatoryTargetPrice = 135, CombinedAiConfidence = 74
        };
        public DailyMarketSentiment? Sentiment = new() { InstrumentId = 10, TradingDate = TodayIst };
        public List<NewsImpact> News = [];
        public LiveQuote Quote = new(100, EntryContextBuilder.NowUtc.AddSeconds(-1));

        public Parts Real()
        {
            Account.TradeMode = TradeMode.Real;
            Account.RealModeEnabledAt = NowUtc.AddDays(-1);
            Account.ApiKey = "key";
            Account.ApiSecret = "secret";
            Token = new AccountAuthToken { AccountId = 1, LoginStatus = LoginStatus.Success, XJwtToken = "jwt", TokenExpiry = NowUtc.AddHours(12) };
            return this;
        }
    }
}
