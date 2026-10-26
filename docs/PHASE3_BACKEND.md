# Phase 3: Backend, Engines and Workers

.NET 10 / ASP.NET Core / EF Core 10 (Npgsql) on PostgreSQL 16. Three processes share one database and talk over PostgreSQL `LISTEN/NOTIFY`.

| Process | Project | Role |
|---|---|---|
| API + Engine | `backend/src/TradeBot.Api` | REST + `/ws/dashboard`, market feed, candles, pattern scan, signals, multi-account dispatcher, OCO engine |
| News worker | `backend/src/TradeBot.Worker.News` | Gemini grounded scans → `trn.company_daily_news` → `trn.daily_market_sentiment` |
| Auth worker | `backend/src/TradeBot.Worker.Auth` | Daily headless Paytm login (TOTP) → `mst.account_auth_tokens` |

All three use `AddSystemd()` (systemd `Type=notify`, journal logging) and read the same `ConnectionStrings:TradeBot` and `Security:MasterKey`.

## 1. Solution layout
| Layer | Contents |
|---|---|
| `TradeBot.Domain` | `mst`/`trn` entities, enums (`[DbValue]` → DB CHECK values), `[Encrypted]` marker |
| `TradeBot.Application` | Validation (Phase 2), market maths, pattern engine, signal/bracket/OCO logic, engine runtime, news + auth services. No I/O dependencies. |
| `TradeBot.Infrastructure` | EF Core context + migrations, repositories, AES-256-GCM secrets, Paytm Money client + WebSocket feed, Gemini, Telegram, `PgEventBus`, Playwright login |
| `TradeBot.Api` | Controllers, JWT admin auth, rate limiting, engine hosted service, dashboard socket |
| `TradeBot.Worker.News` / `.Auth` | Thin `BackgroundService` hosts over `NewsScheduler` / `AuthScheduler` |

## 2. Trading day (IST, trading days from `mst.market_holidays`)
| Time | Who | What |
|---|---|---|
| 08:30 | News worker | Pre-market scan: macro (NIFTY/BANKNIFTY) + F&O/index-weight companies in batches of 30; sentiment recomputed |
| 08:40 | Engine | Pre-open: seeds missing detectors into `mst.chart_patterns`, scrip master sync (`mst.security_master`) |
| 08:45 | Auth worker | Headless login for REAL and data-provider accounts without today's token; retries every 30 s, then Telegram alert with the manual login link |
| 08:50 (or login + 5 min) | Engine | Prep: feed connect, day PnL rows, REAL funds/profile sync, daily stock watchlist (60% catalyst + 40% liquidity), history backfill (5 days) |
| 09:15 – 14:45 | Engine | Entries allowed (`strategy_config.entry_start_time` / `entry_cutoff_time`) |
| every 15 min in session | News worker | Periodic macro scan (`PERIODIC`) |
| on volume spike / manual | News worker | Instrument check (`INTRADAY_SPIKE` / `MANUAL`), 2-min dedup per target |
| 15:15 | Engine | Time square-off of every open trade |
| 15:35 | Engine | End-of-day Telegram summary per account |

## 3. Market data → signal
1. **Feed:** `PaytmMarketFeed` (data-provider account's public token), sharded WebSocket, binary LTP/QUOTE/FULL/index packets, auto-reconnect.
2. **Candles:** `CandleAggregator` builds 1m/3m/5m/15m/1h bars anchored to 09:15; closed bars go to `BarHistory` and are bulk-upserted to `trn.ohlcv_candles` every 2 s.
3. **Patterns:** on each closed trigger-timeframe (5m) bar the `PatternEngine` runs **130 detectors**: 86 candlestick variants (all 61 TA-Lib patterns), 20 classical (zigzag pivots), 12 harmonic (XABCD), 12 SMC (BOS, CHoCH, order blocks, FVG, S/R, CPR). A setup is confirmed only on a breakout close with volume confirmation; confidence is scored and stored in `trn.detected_patterns`.
4. **Signal:** `SignalService` maps a confirmed setup to the ATM option (expiry policy, option VWAP confirmation, OI/PCR score) and builds the mandatory bracket:
   - **Structural:** pattern SL/target repriced with Black-76 at constant IV, SL capped at `max_sl_pct`.
   - **Fixed-% fallback:** used when the structural bracket misses `min_risk_reward_ratio`; anchored to the slippage-adjusted entry.
   - Combined confidence = pattern 50 / news 30 / OI-Greeks 20 (`mst.gemini_news_config` weights). Stored in `trn.signals` with `mandatory_sl_price` / `mandatory_target_price`.

## 4. Multi-account dispatcher
`Dispatcher` fans each signal out to every active account in parallel; work for one account is serialized.
- Per-account bracket from that account's `mst.account_risk_config`.
- Full Phase 2 entry chain (`EntryValidator`), including trade pacing (`max_trades_per_day`, `reentry_cooldown_minutes` after a stop-out).
- Auto-downsized sizing: risk budget, daily-loss headroom, margin, `max_lots_per_trade`, freeze quantity.
- Rejections are written to `trn.system_events`; the signal becomes `DISPATCHED` or `REJECTED`.

## 5. Mandatory SL/Target OCO engine (`TradeManager`)
| Rule | Implementation |
|---|---|
| Every order carries SL + target | `trn.orders.stop_loss_price` / `target_sell_price` NOT NULL + CHECKs; legs inherit the bracket |
| Stop-loss at the exchange | SL-L order placed immediately after the entry fill, limit `sl_limit_buffer_pct` (2%) below the trigger |
| Target | Software-managed: on LTP ≥ target the SL is cancelled first, then the target limit is sent |
| Never naked short | At most one sell leg works at a time; a new exit is placed only after the previous leg is confirmed cancelled |
| Trailing SL | Breakeven at `breakeven_trigger_pct` of the way to target, then steps of `trailing_sl_step_pct`; SL-L modified in place |
| Gap protection | Triggered SL unfilled after `sl_gap_fallback_seconds` → aggressive marketable exit, re-priced every 3 s |
| Fill through the stop | Entry filled at/below SL → immediate rescue exit |
| Risk supervisor | Per-account daily loss → kill switch + square-off; daily profit target → profit lock; all accounts killed → global kill |
| Restart recovery | Open trades and working legs reloaded from `trn.*`; orphan entries cancelled |

PAPER and REAL share this engine. Only the gateway differs:
- **PAPER:** `PaperGateway` + `PaperExchange` simulates limit and SL-L trigger/limit fills on live ticks, with simulated slippage and brokerage.
- **REAL:** `RealGateway` places, modifies and cancels through Paytm, and polls the order book every second.

## 6. News intelligence
- `GeminiNewsClient`: `generateContent` with `google_search` grounding + `responseSchema` JSON. Models that reject both together get a two-step fallback (grounded text, then structuring).
- `NewsIntelligenceService`: sliding-window throttle (`max_calls_per_minute`), URL validation, content-hash dedup, EXTREME items alerted.
- `SentimentAggregator` (per instrument):
  - Impact weights: LOW 0.25, MEDIUM 0.5, HIGH 1, EXTREME 1.5.
  - Relevance: direct news 1. For indexes, a constituent counts weight/10 (capped at 1.5). Macro news counts 0.6 for an index and 0.3 for a stock.
  - Bias: score ≥ +15 is BULLISH, ≤ −15 is BEARISH.
  - EXTREME news halts trading when it affects the instrument directly or comes from a constituent with ≥ 5% weight. EXTREME macro news halts only when `auto_pause_on_extreme_macro` is on.

## 7. API (`/api`, JWT bearer; `POST /api/auth/login` 5/min, mutations 60/min)
| Area | Endpoints |
|---|---|
| Auth | `POST auth/login` |
| Dashboard | `GET dashboard/overview?scope=all\|real\|paper\|{accountId}`, WebSocket `/ws/dashboard?access_token=` (pushes on change, 1 s) |
| Bot | `GET bot/status`, `POST bot/kill-switch` |
| Accounts | `GET/POST accounts`, `GET/PUT accounts/{id}`, `POST accounts/{id}/mode` (typed client-id confirmation), `GET accounts/{id}/auth/login-url`, `POST accounts/{id}/auth/request-token`, `POST accounts/{id}/auth/login`, `POST accounts/{id}/kill-switch` |
| Risk | `GET/PUT accounts/{id}/risk-config` |
| Trades | `GET trades`, `GET trades/{id}` (with order legs), `POST trades/{id}/exit`, `GET signals`, `GET events` |
| Config | `GET/PUT strategy-config`, `GET patterns`, `PUT patterns/{id}`, `PUT patterns/category/{category}`, `GET patterns/detections`, `GET/POST/PUT/DELETE market-holidays` |
| News | `GET news`, `GET news/sentiment`, `GET/PUT news/config`, `POST news/scan` |
| Validation | `GET validation/rules` (rule catalog + form rules for Angular) |
| Charts (Phase 4) | `GET candles`, `GET dashboard/pnl-curve`, `GET instruments`, `GET events?afterId=` |

Secrets (`[Encrypted]`) are write-only. They are stored as `v1:` AES-GCM ciphertext and never serialized; responses expose `hasApiKey`-style flags instead.

## 8. Event channels (`NOTIFY`)
| Channel | Payload | Publisher → listener |
|---|---|---|
| `tradebot_tokens_updated` | account id | Auth worker / API → engine (reload tokens, connect feed) |
| `tradebot_login_requested` | account id | API → Auth worker |
| `tradebot_news_check` | `{id}` (spike) or `manual:{id\|all}` | Engine / API → News worker |
| `tradebot_news_updated` | `all` | News worker → listeners |
| `tradebot_config_changed` | table name | API → engine and workers |

## 9. Configuration
| Key (env: `__` for `:`) | Used by | Notes |
|---|---|---|
| `ConnectionStrings:TradeBot` (or `TRADEBOT_DB`) | all | Npgsql connection string |
| `Security:MasterKey` (or `TRADEBOT_MASTER_KEY`) | all | base64 32-byte AES key for `[Encrypted]` columns |
| `Auth:AdminUser`, `Auth:AdminPasswordHash`, `Auth:JwtKey`, `Auth:TokenHours` | API | hash via `dotnet TradeBot.Api.dll hash-password`; JwtKey base64 ≥ 32 bytes |
| `Engine:Enabled`, `Database:MigrateOnStartup`, `Cors:Origins` | API | |
| `PaytmMoney:*` | API, Auth | `OrderSource`, `SecurityMasterFile`, `StrikeDivisor`, `MaxSubscriptionsPerConnection`, `PriceChartIntervals` |
| `BrokerLogin:*` | Auth | `ChromiumExecutablePath`, `Headless`, timeouts, data-driven `Steps`, failure screenshots |

Business settings live in `mst.*` (edited from the dashboard), not in appsettings.

## 10. Schema changes in Phase 3
| Migration | SQL script | Change |
|---|---|---|
| `Phase3TradePacingAndWatchlist` | `db/migrations/0003_phase3_trade_pacing_watchlist.sql` | trade pacing + SL-L settings, `trn.daily_watchlist` |
| `Phase3NewsPeriodicScan` | `db/migrations/0004_phase3_news_periodic_scan.sql` | `scan_type` adds `PERIODIC` |

## 11. Broker details to verify on first live login
The Paytm Money routes come from the official pyPMClient SDK. These details could not be confirmed, so they are configurable or parsed tolerantly:
- Response field names for funds, orders and price charts (tolerant JSON field lookup).
- The order `source` value (`PaytmMoney:OrderSource`).
- The scrip-master file name and columns (header-alias CSV parsing).
- Strike units (`StrikeDivisor`).
- Login page selectors (`BrokerLogin:Steps`).

Run the first REAL day with one lot (`max_lots_per_trade = 1`).

## 12. Tests
| Suite | Tests | Covers |
|---|---|---|
| `TradeBot.Application.Tests` | 293 | Validation + shared vectors, market maths, 130 pattern detectors, brackets, OCO/trailing/paper exchange, news aggregation, schedulers, broker login |
| `TradeBot.Infrastructure.Tests` | 101 | Migrations on PostgreSQL, every CHECK constraint, form ↔ DB bound parity, encryption, Paytm/Gemini adapters |
| `TradeBot.Api.Tests` | 12 | Real API host on PostgreSQL: auth, validation errors, dashboard socket, and a full PAPER flow (entry → trailing SL → target; stop-loss; re-entry cooldown; dashboard; manual exit; kill switch) |

```bash
cd backend
dotnet test   # needs PostgreSQL; DBs: tradebot_test, tradebot_api_test (TRADEBOT_TEST_DB / TRADEBOT_API_TEST_DB)
```
