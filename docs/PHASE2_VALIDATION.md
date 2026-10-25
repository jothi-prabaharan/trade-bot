# Phase 2: Validation Rules Matrix

Every rule has a stable code. `FX_*` codes are **fixed** (hard-coded, never editable at runtime). `MS_*` codes are **master-driven** (their thresholds come from `mst.*` / `trn.*` rows). `CX_*` codes are form cross-field rules.

| Artifact | Path |
|---|---|
| Rule catalog (codes, kind, message, source) | `backend/src/TradeBot.Application/Validation/RuleCatalog.cs` |
| Fixed limits | `backend/src/TradeBot.Application/Validation/FixedLimits.cs` |
| Pre-trade chain + auto-downsize | `backend/src/TradeBot.Application/Validation/EntryValidator.cs` |
| Order-leg / mode-toggle checks | `backend/src/TradeBot.Application/Validation/AccountRules.cs` |
| Config-drawer rules (served to Angular) | `backend/src/TradeBot.Application/Validation/Forms/FormRuleCatalog.cs` |
| Shared test vectors (C# + Angular) | `shared/validation/validation-vectors.json` |
| Schema support (migration 2) | `mst.market_holidays`, `mst.instruments.freeze_quantity`, tightened CHECKs |

## Phase 2 decisions
| Question | Decision |
|---|---|
| Holiday calendar | `mst.market_holidays` (holidays + special sessions such as Muhurat) |
| Stale tick | Fixed: block entry when option LTP is older than **3 s** |
| News-veto window | Whole session: any opposing HIGH/EXTREME news since the 08:30 scan |
| Over-sized signal | **Auto-downsize** to the largest lot count that passes every limit; reject only if < 1 lot |
| Config sanity bounds | Fixed in code **and** DB CHECK (verified equal by a parity test) |
| UI parity | `GET /api/validation/rules` (built from the C# catalog) + shared JSON test vectors |
| PAPER accounts | Same chain as REAL; only the broker-only rules (credentials, token) are skipped |
| Freeze quantity | `mst.instruments.freeze_quantity`; sizing caps to it, order check rejects above it |

## 1. Fixed validations (`FX_*`)
| Code | Rule | Scope |
|---|---|---|
| `FX_NOT_TRADING_DAY` | Weekend or a holiday row in `mst.market_holidays` | Entry |
| `FX_MARKET_CLOSED` | IST time outside **09:15:00–15:29:59** (special sessions use their own hours) | Entry |
| `FX_STALE_TICK` | `now − tick_time > 3 s` | Entry |
| `FX_INVALID_SEGMENT` | Segment ≠ `D` | Entry, Order |
| `FX_NOT_AN_OPTION` | Contract is not OPTIDX/OPTSTK with CE/PE | Entry |
| `FX_INVALID_TRADE_MODE` | Mode ∉ {PAPER, REAL} | Mode toggle |
| `FX_CONTRACT_EXPIRED` | `expiry_date < today (IST)` | Entry |
| `FX_CONTRACT_MISMATCH` | Contract doesn't belong to the signal's instrument | Entry |
| `FX_ENTRY_MUST_BE_BUY` | Entries must be BUY (option buying only) | Signal, Order |
| `FX_SL_REQUIRED` / `FX_TARGET_REQUIRED` | SL or target is NULL | Signal, Order |
| `FX_SL_NOT_POSITIVE` | `stop_loss_price <= 0` | Signal, Order |
| `FX_SL_NOT_BELOW_ENTRY` | `stop_loss_price >= entry` (also fires if the live price has fallen through the SL) | Signal, Entry |
| `FX_TARGET_NOT_ABOVE_ENTRY` | `target_sell_price <= entry` | Signal, Entry, Order |
| `FX_PRICE_NOT_ON_TICK` | Any price not a multiple of `tick_size` | Order |
| `FX_LEG_ORDER_TYPE_MISMATCH` | STOP_LOSS leg ≠ `SL`, or TARGET_SELL leg ≠ `LMT` | Order |
| `FX_SL_TRIGGER_INVALID` | `SL` order without trigger, or sell limit price > trigger | Order |
| `FX_PARENT_LINK_INVALID` | Entry has a parent, or an exit leg lacks one | Order |
| `FX_TRAILING_SL_LOOSENED` / `FX_TRAILING_SL_AT_TARGET` | Trailing SL moved down, or reached the target | Trailing update |
| `FX_MODE_UNCHANGED` / `FX_REAL_CONFIRMATION_REQUIRED` | No-op toggle, or the typed client ID doesn't match | Mode toggle |

Fixed constants: token-expiry buffer **15 min**; a constituent's news counts toward an index veto at **≥ 5 % index weight**; margin check reserves **2 × brokerage** (entry + exit).

## 2. Master-driven validations (`MS_*`)
| Code | Rule | Source |
|---|---|---|
| `MS_ACCOUNT_INACTIVE` | `is_active = false` | `mst.trading_accounts` |
| `MS_GLOBAL_KILL_SWITCH` | Global kill switch on for today | `trn.daily_bot_state` |
| `MS_ACCOUNT_KILL_SWITCH` | `kill_switch_triggered` | `trn.daily_account_pnl` |
| `MS_PROFIT_LOCKED` | `profit_lock_triggered` | `trn.daily_account_pnl` |
| `MS_DAILY_LOSS_LIMIT_REACHED` | `realized + unrealized − charges <= −max_daily_loss` | `mst.account_risk_config` |
| `MS_API_CREDENTIALS_MISSING` | REAL without API key/secret | `mst.trading_accounts` |
| `MS_TOKEN_INVALID` | REAL without a SUCCESS token valid > 15 min | `mst.account_auth_tokens` |
| `MS_MAX_OPEN_TRADES` | Open trades ≥ `max_open_trades` | `mst.account_risk_config` |
| `MS_DUPLICATE_SIGNAL` | Order already exists for (signal, account) | `trn.orders` |
| `MS_TOGGLE_BLOCKED_OPEN_TRADES` | Mode change while trades are open | `trn.daily_trades` |
| `MS_INSTRUMENT_NOT_TRADABLE` | `is_tradable AND is_active` is false | `mst.instruments` |
| `MS_SECURITY_INACTIVE` / `MS_SECURITY_MASTER_STALE` | Contract inactive, or `synced_on < today` | `mst.security_master` |
| `MS_EXPIRY_DAY_SKIPPED` | Policy `NEAREST_SKIP_EXPIRY_DAY` and expiry is today | `mst.strategy_config` |
| `MS_OUTSIDE_ENTRY_WINDOW` | Outside `entry_start_time..entry_cutoff_time` (09:15–14:45) | `mst.strategy_config` |
| `MS_COMBINED_CONFIDENCE_LOW` | `combined_ai_confidence < min_combined_confidence` (70) | `mst.strategy_config` |
| `MS_PATTERN_INACTIVE` / `MS_PATTERN_NOT_CONFIRMED` | Pattern disabled or breakout not confirmed | `mst.chart_patterns`, `trn.detected_patterns` |
| `MS_PATTERN_CONFIDENCE_LOW` | `confidence_score < min_pattern_confidence` (70) | `mst.account_risk_config` |
| `MS_TRADING_HALT` | `trading_halt_active`, or relevant EXTREME_HALT news (macro only if `auto_pause_on_extreme_macro`) | `trn.daily_market_sentiment`, `trn.company_daily_news`, `mst.gemini_news_config` |
| `MS_OPPOSING_NEWS_VETO` | `block_trade_on_opposing_news` and relevant opposing news today with impact ≥ `veto_min_impact_level` (HIGH) | `mst.account_risk_config`, `mst.gemini_news_config` |
| `MS_PREMIUM_OUT_OF_BAND` | LTP outside `min/max_single_trade_price` (₹30–₹400) | `mst.account_risk_config` |
| `MS_RR_BELOW_MIN` | `(target − limit) / (limit − sl) < min_risk_reward_ratio` (2) | `mst.account_risk_config` |
| `MS_SL_EXCEEDS_MAX_PCT` | `(limit − sl) / limit > max_sl_pct` (25 %) | `mst.account_risk_config` |
| `MS_RISK_BUDGET_TOO_SMALL` · `MS_DAILY_LOSS_HEADROOM_EXHAUSTED` · `MS_INSUFFICIENT_MARGIN` · `MS_FREEZE_QTY_EXCEEDED` | Sizing produces 0 lots; the code names the limit that bound | See sizing below |
| `MS_QTY_NOT_LOT_MULTIPLE` / `MS_QTY_ABOVE_FREEZE` | Order quantity not a lot multiple, or above freeze qty | `mst.security_master`, `mst.instruments` |

**Relevant news** means any of: macro news, news linked to the traded instrument, news on a company whose options are being traded, or news on an index constituent with ≥ 5 % weight. "Opposing" means bearish news for a CE trade, or bullish news for a PE trade.

## 3. Pricing and auto-downsize sizing
1. `limit = roundUp(LTP × (1 + slippage_buffer_pct/100), tick)`; `sl = roundUp(signal SL, tick)` (tighter); `target = roundDown(signal target, tick)` (conservative).
2. RR and SL % are checked at the **limit** price, so slippage that pushes RR below 1:2 rejects the trade. Example: an LTP of 100 with SL 85 and target 130 gives RR 1.90 → rejected.
3. Per-lot risk = `(limit − sl) × lot_size`. The lot count is the minimum of the caps below; the first cap that binds is reported:

| Cap | Lots |
|---|---|
| Risk budget | `floor(floor(capital × risk%) / lot risk) × lot_multiplier)` |
| Daily-loss headroom | `floor((max_daily_loss − max(0, −day_net)) / lot risk)` (profits never extend headroom) |
| Margin | `floor((available_margin − 2 × brokerage) / (limit × lot_size))` |
| Freeze | `floor(freeze_quantity / lot_size)` |
| Config | `max_lots_per_trade` |

Worked default (₹1 L capital, 2 %, NIFTY lot 65, LTP 100, SL 85, target 135): limit **100.50**, risk **₹1,007.50/lot** → **1 lot (65 qty)**, RR **2.23**, margin needed **₹6,572.50**.
`lot_multiplier` is now bounded to **(0, 1]**. It can only scale an account down, so it never breaks the risk-% rule.

## 4. Config-drawer rules (fixed bounds = DB CHECK)
| Form | Field bounds | Cross-field rules |
|---|---|---|
| `riskConfig` | capital > 0; risk % (0, 10]; premium > 0; SL % (0, 100); max SL % (0, 100); breakeven (0, 100]; trailing (0, 100); min RR ≥ 1; daily loss/profit > 0; open trades, max lots ≥ 1 (integers); lot multiplier (0, 1]; confidence 0–100; slippage 0–5 %; brokerage ≥ 0; decimals ≤ column scale | `CX_PREMIUM_BAND`, `CX_MAX_SL_GTE_DEFAULT_SL`, `CX_FALLBACK_RR`, `CX_DAILY_LOSS_LTE_CAPITAL` |
| `tradingAccount` | Name/user required; client ID `[A-Za-z0-9_-]`; email; Indian mobile `[6-9]` + 9 digits; TOTP secret Base32 (≥ 16) | `CX_DATA_PROVIDER_CREDENTIALS` |
| `geminiConfig` | Model name `[a-z0-9.-]`; poll ≥ 10 s; spike > 1×; weights 0–100; veto level enum | `CX_WEIGHTS_SUM_100`, `CX_PREMARKET_BEFORE_OPEN` |
| `strategyConfig` | Enums; confidence 0–100; login retries 0–10 | `CX_SESSION_TIMES` (09:15 ≤ start < cutoff < square-off ≤ 15:29), `CX_AUTH_BEFORE_ENTRY`, `CX_TREND_TF_ABOVE_TRIGGER` |
| `chartPattern` | Code `[A-Z0-9_]`; min candles ≥ 1; weight 0–100; parameters must be a JSON object | — |
| `modeToggle` | Target mode enum | `CX_REAL_CONFIRMATION` (typed client ID must match) |
| `marketHoliday` | ISO date; exchange enum | `CX_SPECIAL_SESSION_TIMES` |

Field error codes: `FIELD_REQUIRED`, `FIELD_TYPE`, `FIELD_MIN`, `FIELD_MAX`, `FIELD_SCALE`, `FIELD_MAX_LENGTH`, `FIELD_PATTERN`, `FIELD_ENUM`. A cross-field rule runs only when none of its fields has a field error, and Angular must follow the same rule.

## 5. How parity is enforced
- **Backend ↔ DB:** `FormDbParityTests` inserts every DB-expressible vector and asserts that PostgreSQL accepts exactly what the validator accepts (47 cases).
- **Backend ↔ Angular:** Angular builds its form validators from `GET /api/validation/rules` (Phase 3 endpoint, payload `ValidationRulesDocument`). It implements each `CX_*` code once and must pass `shared/validation/validation-vectors.json` (94 vectors).
- **Catalog integrity:** tests check that every `RuleCodes` constant is catalogued and every cross-field rule has a failing vector.

Test totals: **175** Application unit tests + **62** PostgreSQL integration tests, all passing.
