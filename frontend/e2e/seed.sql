-- Deterministic dashboard data for Playwright e2e (and local UI checks). Runs on a freshly migrated DB.
-- "Today" is the IST trading date, so the API's default trading_date matches.
BEGIN;

CREATE TEMP TABLE d AS
SELECT (now() AT TIME ZONE 'Asia/Kolkata')::date AS day,
       ((now() AT TIME ZONE 'Asia/Kolkata')::date + time '09:15') AT TIME ZONE 'Asia/Kolkata' AS open_utc;

INSERT INTO mst.trading_accounts (account_name, client_id, user_name, email, phone, trade_mode, is_data_provider, is_active, real_mode_enabled_at, real_mode_enabled_by) VALUES
  ('Real Primary', 'PM12345', 'Asha Trader', 'asha@example.in', '9876543210', 'REAL', true, true, now(), 'seed'),
  ('Paper Alpha', 'PAPER-A', 'Asha Trader', NULL, NULL, 'PAPER', false, true, NULL, NULL),
  ('Paper Beta', 'PAPER-B', 'Asha Trader', NULL, NULL, 'PAPER', false, true, NULL, NULL);

INSERT INTO mst.account_risk_config (account_id, allocated_capital, opening_balance, available_margin, utilised_margin, risk_per_trade_pct,
  min_single_trade_price, max_single_trade_price, default_sl_pct, default_target_pct, min_risk_reward_ratio, max_sl_pct, breakeven_trigger_pct,
  trailing_sl_step_pct, max_daily_loss, max_daily_profit, max_open_trades, max_lots_per_trade, lot_multiplier, min_pattern_confidence,
  block_trade_on_opposing_news, slippage_buffer_pct, simulated_slippage_pct, simulated_brokerage_per_order, max_trades_per_day, reentry_cooldown_minutes)
SELECT account_id, 100000, 100000, CASE client_id WHEN 'PAPER-B' THEN 86955 ELSE 100000 END, CASE client_id WHEN 'PAPER-B' THEN 13045 ELSE 0 END, 2,
  30, 400, 15, 30, 2, 25, 50, 10, 3000, 6000, 1, 10, 1, 70, true, 0.5, 0.25, 20, 3, 15
FROM mst.trading_accounts;

INSERT INTO mst.instruments (symbol, name, instrument_type, exchange, segment, lot_size, tick_size, strike_step, has_weekly_expiry, is_tradable, is_active, freeze_quantity)
VALUES ('NIFTY', 'Nifty 50', 'INDEX', 'NSE', 'D', 65, 0.05, 50, true, true, true, 1800),
       ('BANKNIFTY', 'Nifty Bank', 'INDEX', 'NSE', 'D', 30, 0.05, 100, false, true, true, 900);

INSERT INTO mst.security_master (security_id, instrument_id, exchange, segment, pml_symbol, security_type, strike_price, opt_type, expiry_date, lot_size, tick_size, is_active, synced_on)
SELECT 910001, instrument_id, 'NSE', 'D', 'NIFTY 25000 CE', 'OPTIDX', 25000, 'CE', (SELECT day FROM d) + 2, 65, 0.05, true, (SELECT day FROM d)
FROM mst.instruments WHERE symbol = 'NIFTY';

INSERT INTO mst.chart_patterns (pattern_code, pattern_name, category, direction, min_candles_required, default_weight, require_volume_confirm, require_breakout_close, pattern_parameters, is_active)
VALUES ('BULLISH_ENGULFING', 'Bullish Engulfing', 'CANDLESTICK', 'BULLISH', 2, 60, true, true, '{}', true),
       ('DOUBLE_BOTTOM', 'Double Bottom', 'CLASSICAL', 'BULLISH', 20, 70, true, true, '{"tolerancePct": 0.5}', true),
       ('BAT_BULLISH', 'Bullish Bat', 'HARMONIC', 'BULLISH', 30, 65, false, true, '{}', false);

-- Underlying 5m bars (futures series) and option 1m bars for the day
INSERT INTO trn.ohlcv_candles (instrument_id, security_id, timeframe, candle_time, open, high, low, close, volume)
SELECT i.instrument_id, NULL, '5m', (SELECT open_utc FROM d) + n * interval '5 minutes',
  round((24980 + n * 1.8 + 22 * sin(n / 4.0))::numeric, 2),
  round((24980 + n * 1.8 + 22 * sin(n / 4.0) + 14)::numeric, 2),
  round((24980 + n * 1.8 + 22 * sin(n / 4.0) - 12)::numeric, 2),
  round((24980 + (n + 1) * 1.8 + 22 * sin((n + 1) / 4.0))::numeric, 2),
  50000 + n * 900
FROM generate_series(0, 74) n, mst.instruments i WHERE i.symbol = 'NIFTY';

INSERT INTO trn.ohlcv_candles (instrument_id, security_id, timeframe, candle_time, open, high, low, close, volume)
SELECT i.instrument_id, 910001, '1m', (SELECT open_utc FROM d) + n * interval '1 minute',
  round((96 + n * 0.12 + 3 * sin(n / 7.0))::numeric, 2),
  round((96 + n * 0.12 + 3 * sin(n / 7.0) + 1.1)::numeric, 2),
  round((96 + n * 0.12 + 3 * sin(n / 7.0) - 0.9)::numeric, 2),
  round((96 + (n + 1) * 0.12 + 3 * sin((n + 1) / 7.0))::numeric, 2),
  2000 + n * 15
FROM generate_series(0, 179) n, mst.instruments i WHERE i.symbol = 'NIFTY';

INSERT INTO trn.detected_patterns (instrument_id, pattern_id, timeframe, trend_timeframe, trend_bias, direction, neck_or_breakout_price, structural_sl_price,
  structural_target_price, pivot_coordinates, confidence_score, is_confirmed, breakout_candle_time)
SELECT i.instrument_id, p.pattern_id, '5m', '15m', 'BULLISH', 'BULLISH', 25010, 24960, 25110,
  json_build_array(
    json_build_object('t', (SELECT open_utc FROM d) + interval '25 minutes', 'price', 24968, 'high', false),
    json_build_object('t', (SELECT open_utc FROM d) + interval '40 minutes', 'price', 25010, 'high', true),
    json_build_object('t', (SELECT open_utc FROM d) + interval '45 minutes', 'price', 24992, 'high', false))::jsonb,
  78, true, (SELECT open_utc FROM d) + interval '50 minutes'
FROM mst.instruments i, mst.chart_patterns p WHERE i.symbol = 'NIFTY' AND p.pattern_code = 'BULLISH_ENGULFING';

INSERT INTO trn.signals (detection_id, instrument_id, security_id, signal_type, spot_price, option_delta, entry_option_ltp, mandatory_sl_price,
  mandatory_target_price, sl_method, pattern_score, news_score, oi_greeks_score, combined_ai_confidence, greeks_and_oi, status, rejection_reason, created_at)
SELECT dp.detection_id, dp.instrument_id, 910001, 'BUY', 25012, 0.52, v.ltp, v.sl, v.target, 'FIXED_PCT_FALLBACK', 78, 65, 60, 72, '{}', v.status, v.reason,
  (SELECT open_utc FROM d) + v.at
FROM trn.detected_patterns dp,
  (VALUES (100.00, 85.45, 130.65, 'DISPATCHED', NULL, interval '50 minutes'),
          (104.00, 88.85, 135.85, 'DISPATCHED', NULL, interval '95 minutes'),
          (101.00, 86.30, 131.95, 'REJECTED', '#1: MS_MAX_OPEN_TRADES', interval '100 minutes')) AS v(ltp, sl, target, status, reason, at);

-- Paper Alpha: closed at target
WITH a AS (SELECT account_id FROM mst.trading_accounts WHERE client_id = 'PAPER-A'),
     s AS (SELECT signal_id FROM trn.signals ORDER BY signal_id LIMIT 1),
     e AS (
       INSERT INTO trn.orders (account_id, signal_id, order_leg_type, trade_mode, idempotency_key, txn_type, segment, product, security_id, quantity,
         filled_quantity, price, stop_loss_price, target_sell_price, order_ltp, avg_fill_price, order_type, status, broker_order_no, placed_at, filled_at)
       SELECT a.account_id, s.signal_id, 'ENTRY', 'PAPER', gen_random_uuid(), 'B', 'D', 'I', 910001, 130, 130, 100.50, 85.45, 130.65, 100, 100.25, 'LMT', 'FILLED',
         'PAPER-1', (SELECT open_utc FROM d) + interval '50 minutes', (SELECT open_utc FROM d) + interval '50 minutes'
       FROM a, s RETURNING order_id, account_id, signal_id),
     sl AS (
       INSERT INTO trn.orders (account_id, signal_id, parent_order_id, order_leg_type, trade_mode, idempotency_key, txn_type, segment, product, security_id,
         quantity, filled_quantity, price, trigger_price, stop_loss_price, target_sell_price, order_ltp, order_type, status, broker_order_no, placed_at, cancelled_at)
       SELECT e.account_id, e.signal_id, e.order_id, 'STOP_LOSS', 'PAPER', gen_random_uuid(), 'S', 'D', 'I', 910001, 130, 0, 98.20, 100.25, 100.25, 130.65, 100.25,
         'SL', 'CANCELLED', 'PAPER-2', (SELECT open_utc FROM d) + interval '50 minutes', (SELECT open_utc FROM d) + interval '85 minutes'
       FROM e RETURNING order_id),
     tg AS (
       INSERT INTO trn.orders (account_id, signal_id, parent_order_id, order_leg_type, trade_mode, idempotency_key, txn_type, segment, product, security_id,
         quantity, filled_quantity, price, stop_loss_price, target_sell_price, order_ltp, avg_fill_price, order_type, status, broker_order_no, placed_at, filled_at)
       SELECT e.account_id, e.signal_id, e.order_id, 'TARGET_SELL', 'PAPER', gen_random_uuid(), 'S', 'D', 'I', 910001, 130, 130, 130.65, 85.45, 130.65, 131,
         130.65, 'LMT', 'FILLED', 'PAPER-3', (SELECT open_utc FROM d) + interval '85 minutes', (SELECT open_utc FROM d) + interval '85 minutes'
       FROM e RETURNING order_id)
INSERT INTO trn.daily_trades (account_id, trade_mode, signal_id, security_id, detection_id, entry_order_id, sl_order_id, target_order_id, quantity, lots,
  entry_buy_price, stop_loss_price, trailing_sl_price, target_sell_price, peak_ltp, current_ltp, exit_sell_price, trade_status, gross_pnl,
  brokerage_and_charges, entry_time, exit_time)
SELECT e.account_id, 'PAPER', e.signal_id, 910001, (SELECT detection_id FROM trn.detected_patterns LIMIT 1), e.order_id, sl.order_id, tg.order_id, 130, 2,
  100.25, 85.45, 100.25, 130.65, 131, 130.65, 130.65, 'TARGET_HIT', 3952.00, 76.84,
  (SELECT open_utc FROM d) + interval '50 minutes', (SELECT open_utc FROM d) + interval '85 minutes'
FROM e, sl, tg;

-- Paper Beta: open, SL-L working at the exchange, target managed in software
WITH a AS (SELECT account_id FROM mst.trading_accounts WHERE client_id = 'PAPER-B'),
     s AS (SELECT signal_id FROM trn.signals ORDER BY signal_id OFFSET 1 LIMIT 1),
     e AS (
       INSERT INTO trn.orders (account_id, signal_id, order_leg_type, trade_mode, idempotency_key, txn_type, segment, product, security_id, quantity,
         filled_quantity, price, stop_loss_price, target_sell_price, order_ltp, avg_fill_price, order_type, status, broker_order_no, placed_at, filled_at)
       SELECT a.account_id, s.signal_id, 'ENTRY', 'PAPER', gen_random_uuid(), 'B', 'D', 'I', 910001, 130, 130, 104.50, 88.85, 135.85, 104, 104.25, 'LMT', 'FILLED',
         'PAPER-4', (SELECT open_utc FROM d) + interval '95 minutes', (SELECT open_utc FROM d) + interval '95 minutes'
       FROM a, s RETURNING order_id, account_id, signal_id),
     sl AS (
       INSERT INTO trn.orders (account_id, signal_id, parent_order_id, order_leg_type, trade_mode, idempotency_key, txn_type, segment, product, security_id,
         quantity, filled_quantity, price, trigger_price, stop_loss_price, target_sell_price, order_ltp, order_type, status, broker_order_no, placed_at)
       SELECT e.account_id, e.signal_id, e.order_id, 'STOP_LOSS', 'PAPER', gen_random_uuid(), 'S', 'D', 'I', 910001, 130, 0, 87.05, 88.85, 88.85, 135.85, 104.25,
         'SL', 'TRIGGER_PENDING', 'PAPER-5', (SELECT open_utc FROM d) + interval '95 minutes'
       FROM e RETURNING order_id),
     tg AS (
       INSERT INTO trn.orders (account_id, signal_id, parent_order_id, order_leg_type, trade_mode, idempotency_key, txn_type, segment, product, security_id,
         quantity, filled_quantity, price, stop_loss_price, target_sell_price, order_ltp, order_type, status)
       SELECT e.account_id, e.signal_id, e.order_id, 'TARGET_SELL', 'PAPER', gen_random_uuid(), 'S', 'D', 'I', 910001, 130, 0, 135.85, 88.85, 135.85, 104.25,
         'LMT', 'PENDING'
       FROM e RETURNING order_id)
INSERT INTO trn.daily_trades (account_id, trade_mode, signal_id, security_id, detection_id, entry_order_id, sl_order_id, target_order_id, quantity, lots,
  entry_buy_price, stop_loss_price, trailing_sl_price, target_sell_price, peak_ltp, current_ltp, trade_status, gross_pnl, brokerage_and_charges, entry_time)
SELECT e.account_id, 'PAPER', e.signal_id, 910001, (SELECT detection_id FROM trn.detected_patterns LIMIT 1), e.order_id, sl.order_id, tg.order_id, 130, 2,
  104.25, 88.85, 88.85, 135.85, 112.40, 112.40, 'OPEN', 0, 23.92, (SELECT open_utc FROM d) + interval '95 minutes'
FROM e, sl, tg;

INSERT INTO trn.daily_account_pnl (account_id, trade_mode, opening_balance, available_margin, utilised_margin, total_trades, winning_trades, losing_trades,
  realized_gross_pnl, unrealized_mtm_pnl, total_charges, daily_loss_limit_used_pct, kill_switch_triggered, profit_lock_triggered)
SELECT account_id, trade_mode, 100000, CASE client_id WHEN 'PAPER-B' THEN 86955 ELSE 103875.16 END, CASE client_id WHEN 'PAPER-B' THEN 13045 ELSE 0 END,
  CASE client_id WHEN 'PAPER-A' THEN 1 ELSE 0 END, CASE client_id WHEN 'PAPER-A' THEN 1 ELSE 0 END, 0,
  CASE client_id WHEN 'PAPER-A' THEN 3952.00 ELSE 0 END, 0, CASE client_id WHEN 'PAPER-A' THEN 76.84 ELSE 0 END, 0, false, false
FROM mst.trading_accounts;

INSERT INTO trn.company_daily_news (instrument_id, scan_type, headline, summary, source_urls, news_category, sentiment, sentiment_score, impact_level,
  nifty_weighted_impact, banknifty_weighted_impact, content_hash, gemini_structured_output, analyzed_at)
SELECT i.instrument_id, v.scan, v.headline, 'Seeded for e2e', '["https://example.com/news"]', 'MACRO', v.sentiment, v.score, v.impact, v.score, v.score,
  md5(v.headline), '{}', (SELECT open_utc FROM d) - v.ago
FROM mst.instruments i,
  (VALUES ('PREMARKET', 'RBI holds repo rate, signals easing bias', 'BULLISH', 45, 'HIGH', interval '45 minutes'),
          ('PERIODIC', 'US futures edge higher ahead of CPI', 'BULLISH', 20, 'MEDIUM', interval '-60 minutes')) AS v(scan, headline, sentiment, score, impact, ago)
WHERE i.symbol = 'NIFTY';

INSERT INTO trn.daily_market_sentiment (instrument_id, net_sentiment_score, dominant_bias, active_catalysts, trading_halt_active)
SELECT instrument_id, 38.5, 'BULLISH', '[]', false FROM mst.instruments WHERE symbol = 'NIFTY';

UPDATE trn.daily_trades t SET news_id = (SELECT min(news_id) FROM trn.company_daily_news) WHERE true;

INSERT INTO trn.system_events (account_id, event_type, severity, message, payload, created_at)
SELECT a.account_id, v.type, v.sev, v.msg, '{}', (SELECT open_utc FROM d) + v.at
FROM (VALUES ('PAPER-A', 'TRADE_OPENED', 'INFO', 'Trade opened at 100.25 (SL 85.45, target 130.65)', interval '50 minutes'),
             ('PAPER-A', 'TRADE_CLOSED', 'INFO', 'Trade TARGET_HIT at 130.65; net 3875.16', interval '85 minutes'),
             ('PAPER-B', 'TRADE_OPENED', 'INFO', 'Trade opened at 104.25 (SL 88.85, target 135.85)', interval '95 minutes'),
             ('PAPER-A', 'SIGNAL_REJECTED', 'INFO', 'Signal 3 rejected: MS_MAX_OPEN_TRADES', interval '100 minutes')) AS v(client, type, sev, msg, at)
JOIN mst.trading_accounts a ON a.client_id = v.client;

COMMIT;
