// Mirrors the TradeBot.Api JSON contracts (camelCase; enums use their DB values).

export type TradeMode = 'PAPER' | 'REAL';
export type OptionType = 'CE' | 'PE';
export type MarketBias = 'BULLISH' | 'BEARISH' | 'NEUTRAL';
export type TradeStatus = 'OPEN' | 'TARGET_HIT' | 'SL_HIT' | 'TRAILING_SL_HIT' | 'TIME_SQUARE_OFF' | 'KILL_SWITCH_EXIT' | 'MANUAL_EXIT';
export type OrderStatus = 'PENDING' | 'PLACED' | 'OPEN' | 'TRIGGER_PENDING' | 'PARTIALLY_FILLED' | 'FILLED' | 'CANCELLED' | 'REJECTED' | 'FAILED';
export type SignalStatus = 'NEW' | 'DISPATCHED' | 'REJECTED' | 'EXPIRED';
export type Severity = 'INFO' | 'WARNING' | 'CRITICAL';
export type ImpactLevel = 'LOW' | 'MEDIUM' | 'HIGH' | 'EXTREME_HALT';
export type TokenStatus = 'NOT_REQUIRED' | 'MISSING' | 'FAILED' | 'EXPIRED' | 'EXPIRING' | 'VALID' | 'PENDING';
export type MarketSession = 'HOLIDAY' | 'PRE_OPEN' | 'OPEN' | 'ENTRY_CLOSED' | 'CLOSED';
export type PatternCategory = 'CANDLESTICK' | 'CLASSICAL' | 'HARMONIC' | 'SMC_STRUCTURE';
export type Scope = 'all' | 'real' | 'paper' | `${number}`;

export interface ApiError { code: string; message: string; field?: string | null }
export interface ApiErrorBody { errors: ApiError[] }

export interface LoginResponse { token: string; expiresAtUtc: string; user: string }

export interface BotStatus {
  serverTimeUtc: string; marketSession: MarketSession; feedConnected: boolean; lastTickUtc: string | null;
  globalKillSwitch: boolean; globalReason: string | null; openTrades: number; prepared: boolean;
}

export interface AccountCard {
  accountId: number; accountName: string; clientId: string; userName: string; mode: TradeMode; isDataProvider: boolean; isActive: boolean;
  tokenStatus: TokenStatus; tokenExpiryUtc: string | null; tokenSecondsLeft: number | null;
  openingBalance: number; availableMargin: number; utilisedMargin: number; allocatedCapital: number; fundsSyncedAtUtc: string | null;
}

export interface PnlSummary {
  realizedGross: number; unrealizedMtm: number; charges: number; net: number; totalTrades: number; wins: number; losses: number;
  winRatePct: number; maxDailyLoss: number; lossLimitUsedPct: number; killSwitch: boolean; profitLock: boolean; haltReason: string | null;
}

export interface AccountPnl { accountId: number; accountName: string; mode: TradeMode; pnl: PnlSummary }

export interface TradeRow {
  tradeId: number; accountId: number; accountName: string; mode: TradeMode; entryTimeUtc: string; exitTimeUtc: string | null;
  contract: string; optType: OptionType | null; quantity: number; lots: number; entryPrice: number; stopLoss: number; trailingSl: number;
  target: number; ltp: number | null; exitPrice: number | null; progressPct: number; status: TradeStatus;
  patternCode: string | null; patternName: string | null; newsHeadline: string | null; newsSentiment: MarketBias | null;
  grossPnl: number; charges: number; netPnl: number; netPnlPct: number; securityId: number; instrumentId: number | null; patternTimeframe: string | null;
}

export interface DashboardOverview { bot: BotStatus; accounts: AccountCard[]; consolidated: PnlSummary; perAccount: AccountPnl[]; trades: TradeRow[] }

export interface SystemEvent { eventId: number; accountId: number | null; eventType: string; severity: Severity; message: string; payload: string; createdAt: string }

export type SocketMessage = { type: 'overview'; data: DashboardOverview } | { type: 'events'; data: SystemEvent[] };

export interface AccountDto {
  accountId: number; accountName: string; clientId: string; userName: string; email: string | null; phone: string | null;
  brokerBranch: string | null; loginId: string | null; tradeMode: TradeMode; isDataProvider: boolean; isActive: boolean;
  hasApiKey: boolean; hasApiSecret: boolean; hasTotpSecret: boolean; hasLoginPassword: boolean; hasLoginPin: boolean;
  realModeEnabledAt: string | null; version: number;
}

export interface RiskConfig {
  configId: number; accountId: number; allocatedCapital: number; openingBalance: number; availableMargin: number; utilisedMargin: number;
  riskPerTradePct: number; minSingleTradePrice: number; maxSingleTradePrice: number; defaultSlPct: number; defaultTargetPct: number;
  minRiskRewardRatio: number; maxSlPct: number; breakevenTriggerPct: number; trailingSlStepPct: number; maxDailyLoss: number;
  maxDailyProfit: number; maxOpenTrades: number; maxLotsPerTrade: number; maxTradesPerDay: number; reentryCooldownMinutes: number;
  lotMultiplier: number; minPatternConfidence: number; blockTradeOnOpposingNews: boolean; slippageBufferPct: number;
  simulatedSlippagePct: number; simulatedBrokeragePerOrder: number; fundsSyncedAt: string | null; version: number;
}

export interface StrategyConfig {
  strategyConfigId: number; strikeSelection: string; expiryPolicy: string; product: string; trendTimeframe: string; triggerTimeframe: string;
  requireOptionConfirmation: boolean; minCombinedConfidence: number; entryStartTime: string; entryCutoffTime: string; squareOffTime: string;
  authLoginTime: string; authMaxRetries: number; slLimitBufferPct: number; slGapFallbackSeconds: number; entryFillTimeoutSeconds: number;
  stockWatchlistSize: number; historyBackfillDays: number;
}

export interface GeminiConfig {
  geminiConfigId: number; modelName: string; useSearchGrounding: boolean; premarketScanTime: string; intradayPollSeconds: number;
  volumeSpikeTriggerMultiplier: number; patternWeightPct: number; newsWeightPct: number; oiGreeksWeightPct: number;
  vetoMinImpactLevel: ImpactLevel; autoPauseOnExtremeMacro: boolean; maxCallsPerMinute: number;
}
export interface GeminiConfigResponse { config: GeminiConfig; hasApiKey: boolean }

export interface ChartPattern {
  patternId: number; patternCode: string; patternName: string; category: PatternCategory; direction: string; minCandlesRequired: number;
  defaultWeight: number; requireVolumeConfirm: boolean; requireBreakoutClose: boolean; patternParameters: string; isActive: boolean;
}
export interface PatternRow { pattern: ChartPattern; hasDetector: boolean }

export interface MarketHoliday {
  holidayId: number; holidayDate: string; exchange: string; description: string; isSpecialSession: boolean;
  sessionOpenTime: string | null; sessionCloseTime: string | null;
}

export interface InstrumentDto { instrumentId: number; symbol: string; name: string | null; instrumentType: 'INDEX' | 'STOCK'; lotSize: number; tickSize: number; isTradable: boolean; isActive: boolean }

export interface Signal {
  signalId: number; tradingDate: string; instrumentId: number; securityId: number; signalType: string; spotPrice: number; entryOptionLtp: number;
  mandatorySlPrice: number; mandatoryTargetPrice: number; riskRewardRatio: number; slMethod: string; patternScore: number; newsScore: number;
  oiGreeksScore: number; combinedAiConfidence: number; status: SignalStatus; rejectionReason: string | null; createdAt: string;
  security?: { pmlSymbol: string } | null; detection?: { pattern?: { patternCode: string; patternName: string } | null; timeframe: string } | null;
}

export interface NewsItem {
  newsId: number; tradingDate: string; companyId: number | null; instrumentId: number | null; scanType: string; headline: string; summary: string | null;
  sourceUrls: string; newsCategory: string; sentiment: MarketBias; sentimentScore: number; impactLevel: ImpactLevel; publishedAt: string | null;
  analyzedAt: string; company?: { nseSymbol: string; companyName: string } | null;
}

export interface Sentiment {
  sentimentId: number; tradingDate: string; instrumentId: number; netSentimentScore: number; dominantBias: MarketBias;
  activeCatalysts: string; tradingHaltActive: boolean; haltReason: string | null;
}

export interface Order {
  orderId: number; orderLegType: 'ENTRY' | 'STOP_LOSS' | 'TARGET_SELL' | 'EMERGENCY_EXIT'; txnType: 'B' | 'S'; quantity: number; filledQuantity: number;
  price: number; triggerPrice: number | null; stopLossPrice: number; targetSellPrice: number; avgFillPrice: number | null; orderType: 'LMT' | 'SL';
  status: OrderStatus; brokerOrderNo: string | null; rejectionReason: string | null; placedAt: string | null; filledAt: string | null; cancelledAt: string | null;
}

export interface TradeDetail {
  trade: {
    tradeId: number; securityId: number; entryTime: string; exitTime: string | null; entryBuyPrice: number; stopLossPrice: number; trailingSlPrice: number;
    targetSellPrice: number; exitSellPrice: number | null; tradeStatus: TradeStatus;
    security?: { instrumentId: number; pmlSymbol: string; tickSize: number } | null;
    detection?: { timeframe: string; pivotCoordinates: string; neckOrBreakoutPrice: number; breakoutCandleTime: string | null; pattern?: { patternName: string } | null } | null;
  };
  orders: Order[];
}

export interface Candle { time: string; open: number; high: number; low: number; close: number; volume: number }
export interface CurvePoint { timeUtc: string; value: number }
export interface PnlCurve { realized: CurvePoint[]; net: CurvePoint[] }
