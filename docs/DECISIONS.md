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
