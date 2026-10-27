import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AccountDto, ApiError, Candle, ChartPattern, DashboardOverview, GeminiConfigResponse, InstrumentDto, LoginResponse, MarketHoliday,
  NewsItem, PatternRow, PnlCurve, RiskConfig, Scope, Sentiment, Signal, StrategyConfig, SystemEvent, TradeDetail,
} from './models';
import { ValidationRulesDocument } from './validation/rules';

type Body = Record<string, unknown>;

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  login(username: string, password: string, remember: boolean): Observable<LoginResponse> {
    return this.http.post<LoginResponse>('/api/auth/login', { username, password, remember });
  }

  rules(): Observable<ValidationRulesDocument> { return this.http.get<ValidationRulesDocument>('/api/validation/rules'); }

  overview(scope: Scope): Observable<DashboardOverview> { return this.http.get<DashboardOverview>('/api/dashboard/overview', { params: { scope } }); }
  pnlCurve(scope: Scope): Observable<PnlCurve> { return this.http.get<PnlCurve>('/api/dashboard/pnl-curve', { params: { scope } }); }
  globalKill(reason: string): Observable<void> { return this.http.post<void>('/api/bot/kill-switch', { reason }); }

  accounts(): Observable<AccountDto[]> { return this.http.get<AccountDto[]>('/api/accounts'); }
  createAccount(body: Body): Observable<AccountDto> { return this.http.post<AccountDto>('/api/accounts', body); }
  updateAccount(id: number, body: Body): Observable<AccountDto> { return this.http.put<AccountDto>(`/api/accounts/${id}`, body); }
  setMode(id: number, targetMode: string, confirmClientId: string | null): Observable<AccountDto> {
    return this.http.post<AccountDto>(`/api/accounts/${id}/mode`, { targetMode, confirmClientId });
  }
  loginUrl(id: number): Observable<{ url: string }> { return this.http.get<{ url: string }>(`/api/accounts/${id}/auth/login-url`); }
  exchangeRequestToken(id: number, requestToken: string): Observable<{ tokenExpiry: string }> {
    return this.http.post<{ tokenExpiry: string }>(`/api/accounts/${id}/auth/request-token`, { requestToken });
  }
  triggerLogin(id: number): Observable<void> { return this.http.post<void>(`/api/accounts/${id}/auth/login`, {}); }
  accountKill(id: number, reason: string): Observable<void> { return this.http.post<void>(`/api/accounts/${id}/kill-switch`, { reason }); }

  riskConfig(id: number): Observable<RiskConfig> { return this.http.get<RiskConfig>(`/api/accounts/${id}/risk-config`); }
  updateRiskConfig(id: number, body: Body): Observable<RiskConfig> { return this.http.put<RiskConfig>(`/api/accounts/${id}/risk-config`, body); }

  strategy(): Observable<StrategyConfig> { return this.http.get<StrategyConfig>('/api/strategy-config'); }
  updateStrategy(body: Body): Observable<StrategyConfig> { return this.http.put<StrategyConfig>('/api/strategy-config', body); }

  gemini(): Observable<GeminiConfigResponse> { return this.http.get<GeminiConfigResponse>('/api/news/config'); }
  updateGemini(body: Body): Observable<GeminiConfigResponse> { return this.http.put<GeminiConfigResponse>('/api/news/config', body); }
  news(limit = 100): Observable<NewsItem[]> { return this.http.get<NewsItem[]>('/api/news', { params: { limit } }); }
  sentiment(): Observable<Sentiment[]> { return this.http.get<Sentiment[]>('/api/news/sentiment'); }
  scanNews(instrumentId: number | null): Observable<void> { return this.http.post<void>('/api/news/scan', { instrumentId }); }

  patterns(): Observable<PatternRow[]> { return this.http.get<PatternRow[]>('/api/patterns'); }
  updatePattern(id: number, body: Body): Observable<ChartPattern> { return this.http.put<ChartPattern>(`/api/patterns/${id}`, body); }
  togglePatternCategory(category: string, active: boolean): Observable<{ updated: number }> {
    return this.http.put<{ updated: number }>(`/api/patterns/category/${category}`, {}, { params: { active } });
  }

  holidays(): Observable<MarketHoliday[]> { return this.http.get<MarketHoliday[]>('/api/market-holidays'); }
  createHoliday(body: Body): Observable<MarketHoliday> { return this.http.post<MarketHoliday>('/api/market-holidays', body); }
  updateHoliday(id: number, body: Body): Observable<MarketHoliday> { return this.http.put<MarketHoliday>(`/api/market-holidays/${id}`, body); }
  deleteHoliday(id: number): Observable<void> { return this.http.delete<void>(`/api/market-holidays/${id}`); }

  trade(id: number): Observable<TradeDetail> { return this.http.get<TradeDetail>(`/api/trades/${id}`); }
  exitTrade(id: number): Observable<void> { return this.http.post<void>(`/api/trades/${id}/exit`, {}); }
  signals(): Observable<Signal[]> { return this.http.get<Signal[]>('/api/signals'); }
  events(limit = 100): Observable<SystemEvent[]> { return this.http.get<SystemEvent[]>('/api/events', { params: { limit } }); }
  instruments(): Observable<InstrumentDto[]> { return this.http.get<InstrumentDto[]>('/api/instruments'); }

  candles(instrumentId: number, securityId: number | null, timeframe: string, date?: string): Observable<Candle[]> {
    let params = new HttpParams().set('instrumentId', instrumentId).set('timeframe', timeframe);
    if (securityId !== null) params = params.set('securityId', securityId);
    if (date) params = params.set('date', date);
    return this.http.get<Candle[]>('/api/candles', { params });
  }
}

/** Server-side errors in the `{ errors: [...] }` shape (validation, conflicts, rate limits). */
export function apiErrors(e: unknown): ApiError[] {
  if (e instanceof HttpErrorResponse) {
    const body = e.error as { errors?: ApiError[] } | null;
    if (body?.errors?.length) return body.errors;
    if (e.status === 0) return [{ code: 'NETWORK', message: 'Cannot reach the server.' }];
    if (e.status === 429) return [{ code: 'RATE_LIMITED', message: 'Too many requests; wait a moment and retry.' }];
    if (e.status === 404) return [{ code: 'NOT_FOUND', message: 'Not found (it may have closed already).' }];
    return [{ code: `HTTP_${e.status}`, message: e.message }];
  }
  return [{ code: 'UNKNOWN', message: String(e) }];
}

export function apiErrorText(e: unknown): string {
  return apiErrors(e).map((x) => x.message).join(' ');
}
