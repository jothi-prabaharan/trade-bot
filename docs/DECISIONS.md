# Trade Bot — Phase 0 Configuration Decisions

## Stack
| Item | Decision |
|---|---|
| Backend | .NET 10 LTS, ASP.NET Core Web API |
| Data access | EF Core 10 + Npgsql, code-first migrations (`mst` / `trn` schemas via Fluent API) |
| Workers | All C#: Gemini (GenAI REST), Microsoft.Playwright for .NET, as `BackgroundService`s |
| Frontend | Angular (latest, standalone components), RxJS, Bootstrap 5, FontAwesome |
| Repo layout | Monorepo: `backend/`, `frontend/`, `db/`, `deploy/`, `docs/` |
| Secrets at rest | App-side AES-256-GCM via EF Core value converters; master key from root-only env file |
| Git | Commit directly to `main`; no new branches |

## Accounts & Modes
| Item | Decision |
|---|---|
| Accounts | 1 REAL (market-data/WebSocket provider) + 2 PAPER |
| Mode toggle | Live, no restart; blocked while account has OPEN trades; confirm dialog for REAL |
| Go-live | All accounts seeded as PAPER; REAL enabled only via manual dashboard confirmation |
| Lot sizing | Independent per account: `floor((capital × risk%) / (SL pts × lot_size))`, capped by `max_lots_per_trade` and margin |
| Paper realism | Slippage + ₹20/order brokerage + STT, exchange txn, SEBI, stamp duty, GST; Gross & Net PnL |

## Capital & Risk (per account, editable in `mst.account_risk_config`)
| Item | Value |
|---|---|
| Allocated capital | ₹1,00,000 (REAL and each PAPER) |
| Risk per trade | 2% (₹2,000) — also capped by remaining daily loss headroom |
| Premium range | ₹30 – ₹400 |
| SL/Target method | Hybrid: pattern structural via Delta (capped by `max_sl_pct`), fallback to fixed % |
| Fixed fallback | SL 15% / Target 30% / min RR 1:2 |
| OCO | Exchange SL-L order on entry fill + software-monitored Target (LMT on hit, cancels SL) |
| Trailing SL | Breakeven at 50% of distance to target, then trail 10% of further move |
| Max daily loss | 3% (₹3,000) → square off + halt; global kill switch when all accounts halt |
| Daily profit lock | 6% (₹6,000) → no new entries; open trades ride trailing SL |
| Max open trades | 1 per account |

## Chart Pattern Engine
| Item | Decision |
|---|---|
| Active families | Candlestick (60+), Classical geometric |
| Seeded but inactive | Harmonic, SMC / price action (`is_active = false`, toggleable) |
| Chart source | Underlying spot/futures; option premium confirms (above VWAP or breaks swing) |
| Timeframes | 15m trend bias + 5m pattern breakout on candle close |
| Min combined confidence | 70 |

## Gemini News Intelligence
| Item | Decision |
|---|---|
| Universe | All NSE F&O stocks + NIFTY50/BANKNIFTY/FINNIFTY constituents with index weights |
| Schedule | 08:30 IST pre-market scan + intraday grounding on volume spikes |
| Veto | Hard veto on opposing HIGH + EXTREME news; EXTREME_HALT pauses the instrument |
| Model | Latest Gemini Flash (configurable `model_name`) |
| Weights | Pattern 50 / News 30 / OI-Greeks 20 |

## Execution
| Item | Decision |
|---|---|
| Instruments | NIFTY, BANKNIFTY, F&O stock options |
| Expiry | Nearest (NIFTY weekly, others monthly); roll to next expiry on expiry day |
| Strike | ATM |
| Slippage buffer (REAL LMT) | 0.5%, rounded to tick size |
| Product | Intraday (`I`) |
| Session | Entries 09:15–14:45 IST; time square-off 15:15 IST |
| Auth | 08:45 IST headless Playwright + TOTP, 3 retries, then Telegram alert + manual token paste on dashboard |
| Alerts | Telegram |

## Phase 2 — Validation
| Item | Decision |
|---|---|
| Holiday calendar | `mst.market_holidays` table (holidays + special sessions) |
| Stale tick | Block entries when option LTP is older than 3 s (fixed) |
| News-veto window | Whole trading session since the 08:30 pre-market scan |
| Over-sized signals | Auto-downsize to the largest passing lot count; reject below 1 lot |
| Config sanity bounds | Fixed in code + DB CHECK |
| UI parity | `GET /api/validation/rules` + shared JSON test vectors |
| PAPER validation | Identical to REAL except broker-only rules (credentials, token) |
| Freeze quantity | `mst.instruments.freeze_quantity`, cap/reject (no slicing) |

## Phase 4 — Dashboard
| Item | Decision |
|---|---|
| Framework | Angular 22 standalone, zoneless; signals for view state, RxJS for HTTP/WebSocket streams |
| Layout | One unified screen; every config editor opens in a Bootstrap offcanvas drawer |
| Theme | Dark by default with a light toggle (Bootstrap 5.3 color modes), remembered per browser |
| Hosting | nginx serves the build and proxies `/api` + `/ws` to the API (same origin, no CORS) |
| Charts | TradingView lightweight-charts: trade drawer (option + underlying candles, entry/SL/trailing/target lines, pattern pivots) and intraday PnL curve |
| Confirmations | Manual exit: confirm dialog. Kill switch: type `KILL` + reason. PAPER→REAL: type the client ID. REAL→PAPER: confirm dialog |
| Alerts | Toasts for trade, rejection and login events; sound on critical events; mute toggle |
| Tests | Vitest (unit + all shared validation vectors) and Playwright e2e against the real API with a PAPER account |

## Phase 5 — Deployment
| Item | Decision |
|---|---|
| OS | Ubuntu 24.04 LTS; PostgreSQL 16 and .NET 10 from Ubuntu, Node 24 from NodeSource |
| Server | Shared 64 GB / 16-core host; TradeBot (incl. PostgreSQL) capped at **16 GB / 4 cores** via `tradebot.slice` |
| Access | nginx + Let's Encrypt on a domain, IP allowlist, admin login; ufw 22/80/443 |
| Deploy | Build on the server from git (`deploy.sh`), release dirs + `current` symlink, auto-rollback on failed health check, market-hours guard |
| Backups | `pg_dump -n mst -n trn` at 15:50 and 23:45 IST + pre-deploy; 14 days local, rclone (crypt) offsite 30 days |
| F&O universe | Bundled CSV (220 stocks + NIFTY/BANKNIFTY/FINNIFTY weights, re-importable) + daily scrip-master reconciliation |
| Monitoring | systemd restarts + 1-minute health check with Telegram alerts on change |
| Updates | Security-only unattended upgrades at 22:30 IST, reboot if needed at 23:30 IST |
| Time | Asia/Kolkata system time zone, chrony (NPL India + in.pool.ntp.org); database stores UTC |
