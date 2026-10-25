using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMstTrnSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mst");

            migrationBuilder.EnsureSchema(
                name: "trn");

            migrationBuilder.CreateTable(
                name: "alert_channels",
                schema: "mst",
                columns: table => new
                {
                    alert_channel_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    channel_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    channel_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    bot_token = table.Column<string>(type: "text", nullable: true),
                    chat_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notify_on_entry = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_exit = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_critical = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_channels", x => x.alert_channel_id);
                    table.CheckConstraint("ck_alert_channels_channel_type", "channel_type IN ('TELEGRAM', 'EMAIL')");
                    table.CheckConstraint("ck_alert_channels_telegram_complete", "NOT is_active OR channel_type <> 'TELEGRAM' OR (bot_token IS NOT NULL AND chat_id IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "charge_rates",
                schema: "mst",
                columns: table => new
                {
                    charge_rate_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    security_type = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    brokerage_per_order = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    stt_sell_pct = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    exchange_txn_pct = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    sebi_fee_pct = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    stamp_duty_buy_pct = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    gst_pct = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_rates", x => x.charge_rate_id);
                    table.CheckConstraint("ck_charge_rates_non_negative", "brokerage_per_order >= 0 AND stt_sell_pct >= 0 AND exchange_txn_pct >= 0 AND sebi_fee_pct >= 0 AND stamp_duty_buy_pct >= 0 AND gst_pct >= 0");
                    table.CheckConstraint("ck_charge_rates_security_type", "security_type IN ('INDEX', 'EQUITY', 'FUTIDX', 'FUTSTK', 'OPTIDX', 'OPTSTK')");
                });

            migrationBuilder.CreateTable(
                name: "chart_patterns",
                schema: "mst",
                columns: table => new
                {
                    pattern_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pattern_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    pattern_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    category = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    direction = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    min_candles_required = table.Column<int>(type: "integer", nullable: false),
                    default_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    require_volume_confirm = table.Column<bool>(type: "boolean", nullable: false),
                    require_breakout_close = table.Column<bool>(type: "boolean", nullable: false),
                    pattern_parameters = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chart_patterns", x => x.pattern_id);
                    table.CheckConstraint("ck_chart_patterns_category", "category IN ('CANDLESTICK', 'CLASSICAL', 'HARMONIC', 'SMC_STRUCTURE')");
                    table.CheckConstraint("ck_chart_patterns_default_weight", "default_weight BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_chart_patterns_direction", "direction IN ('BULLISH', 'BEARISH', 'NEUTRAL', 'BIDIRECTIONAL')");
                    table.CheckConstraint("ck_chart_patterns_min_candles", "min_candles_required >= 1");
                });

            migrationBuilder.CreateTable(
                name: "daily_bot_state",
                schema: "trn",
                columns: table => new
                {
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false),
                    global_kill_switch_triggered = table.Column<bool>(type: "boolean", nullable: false),
                    triggered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    triggered_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_bot_state", x => x.trading_date);
                    table.CheckConstraint("ck_daily_bot_state_kill_switch", "NOT global_kill_switch_triggered OR triggered_at IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "gemini_news_config",
                schema: "mst",
                columns: table => new
                {
                    gemini_config_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gemini_api_key = table.Column<string>(type: "text", nullable: true),
                    model_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    use_search_grounding = table.Column<bool>(type: "boolean", nullable: false),
                    premarket_scan_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    intraday_poll_seconds = table.Column<int>(type: "integer", nullable: false),
                    volume_spike_trigger_multiplier = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    pattern_weight_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    news_weight_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    oi_greeks_weight_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    veto_min_impact_level = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    auto_pause_on_extreme_macro = table.Column<bool>(type: "boolean", nullable: false),
                    max_calls_per_minute = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gemini_news_config", x => x.gemini_config_id);
                    table.CheckConstraint("ck_gemini_news_config_polling", "intraday_poll_seconds >= 10 AND volume_spike_trigger_multiplier > 1 AND max_calls_per_minute >= 1");
                    table.CheckConstraint("ck_gemini_news_config_veto_min_impact_level", "veto_min_impact_level IN ('LOW', 'MEDIUM', 'HIGH', 'EXTREME_HALT')");
                    table.CheckConstraint("ck_gemini_news_config_weights_range", "pattern_weight_pct BETWEEN 0 AND 100 AND news_weight_pct BETWEEN 0 AND 100 AND oi_greeks_weight_pct BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_gemini_news_config_weights_sum", "pattern_weight_pct + news_weight_pct + oi_greeks_weight_pct = 100");
                });

            migrationBuilder.CreateTable(
                name: "instruments",
                schema: "mst",
                columns: table => new
                {
                    instrument_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    instrument_type = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    exchange = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    segment = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    lot_size = table.Column<int>(type: "integer", nullable: false),
                    tick_size = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    strike_step = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    underlying_security_id = table.Column<long>(type: "bigint", nullable: true),
                    has_weekly_expiry = table.Column<bool>(type: "boolean", nullable: false),
                    is_tradable = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instruments", x => x.instrument_id);
                    table.CheckConstraint("ck_instruments_exchange", "exchange IN ('NSE', 'BSE')");
                    table.CheckConstraint("ck_instruments_instrument_type", "instrument_type IN ('INDEX', 'STOCK')");
                    table.CheckConstraint("ck_instruments_lot_tick", "lot_size > 0 AND tick_size > 0");
                    table.CheckConstraint("ck_instruments_segment", "segment IN ('D', 'E')");
                    table.CheckConstraint("ck_instruments_strike_step", "strike_step IS NULL OR strike_step > 0");
                });

            migrationBuilder.CreateTable(
                name: "strategy_config",
                schema: "mst",
                columns: table => new
                {
                    strategy_config_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    strike_selection = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    expiry_policy = table.Column<string>(type: "character varying(23)", maxLength: 23, nullable: false),
                    product = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    trend_timeframe = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    trigger_timeframe = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    require_option_confirmation = table.Column<bool>(type: "boolean", nullable: false),
                    min_combined_confidence = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    entry_start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    entry_cutoff_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    square_off_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    auth_login_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    auth_max_retries = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_config", x => x.strategy_config_id);
                    table.CheckConstraint("ck_strategy_config_auth", "auth_login_time < entry_start_time AND auth_max_retries BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_strategy_config_confidence", "min_combined_confidence BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_strategy_config_expiry_policy", "expiry_policy IN ('NEAREST_SKIP_EXPIRY_DAY', 'NEAREST', 'MONTHLY_ONLY')");
                    table.CheckConstraint("ck_strategy_config_product", "product IN ('I', 'M', 'B')");
                    table.CheckConstraint("ck_strategy_config_session_times", "entry_start_time >= TIME '09:15' AND entry_cutoff_time > entry_start_time AND square_off_time > entry_cutoff_time AND square_off_time <= TIME '15:29'");
                    table.CheckConstraint("ck_strategy_config_strike_selection", "strike_selection IN ('ATM', 'ITM1', 'OTM1', 'DELTA_TARGET')");
                    table.CheckConstraint("ck_strategy_config_timeframes", "trend_timeframe <> trigger_timeframe");
                    table.CheckConstraint("ck_strategy_config_trend_timeframe", "trend_timeframe IN ('1m', '3m', '5m', '15m', '1h', '1d')");
                    table.CheckConstraint("ck_strategy_config_trigger_timeframe", "trigger_timeframe IN ('1m', '3m', '5m', '15m', '1h', '1d')");
                });

            migrationBuilder.CreateTable(
                name: "trading_accounts",
                schema: "mst",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    client_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    user_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    broker_branch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    trade_mode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    is_data_provider = table.Column<bool>(type: "boolean", nullable: false),
                    login_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    login_password = table.Column<string>(type: "text", nullable: true),
                    login_pin = table.Column<string>(type: "text", nullable: true),
                    api_key = table.Column<string>(type: "text", nullable: true),
                    api_secret = table.Column<string>(type: "text", nullable: true),
                    totp_secret = table.Column<string>(type: "text", nullable: true),
                    real_mode_enabled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    real_mode_enabled_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trading_accounts", x => x.account_id);
                    table.CheckConstraint("ck_trading_accounts_real_mode_confirmed", "trade_mode <> 'REAL' OR real_mode_enabled_at IS NOT NULL");
                    table.CheckConstraint("ck_trading_accounts_trade_mode", "trade_mode IN ('PAPER', 'REAL')");
                });

            migrationBuilder.CreateTable(
                name: "daily_market_sentiment",
                schema: "trn",
                columns: table => new
                {
                    sentiment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    net_sentiment_score = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    dominant_bias = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    active_catalysts = table.Column<string>(type: "jsonb", nullable: false),
                    trading_halt_active = table.Column<bool>(type: "boolean", nullable: false),
                    halt_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_market_sentiment", x => x.sentiment_id);
                    table.CheckConstraint("ck_daily_market_sentiment_dominant_bias", "dominant_bias IN ('BULLISH', 'BEARISH', 'NEUTRAL')");
                    table.CheckConstraint("ck_daily_market_sentiment_score", "net_sentiment_score BETWEEN -100 AND 100");
                    table.ForeignKey(
                        name: "fk_daily_market_sentiment_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_master",
                schema: "mst",
                columns: table => new
                {
                    security_id = table.Column<long>(type: "bigint", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    exchange = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    segment = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    pml_symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    security_type = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    strike_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    opt_type = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    lot_size = table.Column<int>(type: "integer", nullable: false),
                    tick_size = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    synced_on = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_master", x => x.security_id);
                    table.CheckConstraint("ck_security_master_exchange", "exchange IN ('NSE', 'BSE')");
                    table.CheckConstraint("ck_security_master_future_expiry", "security_type NOT IN ('FUTIDX', 'FUTSTK') OR expiry_date IS NOT NULL");
                    table.CheckConstraint("ck_security_master_lot_tick", "lot_size > 0 AND tick_size > 0");
                    table.CheckConstraint("ck_security_master_opt_type", "opt_type IN ('CE', 'PE')");
                    table.CheckConstraint("ck_security_master_option_fields", "(security_type IN ('OPTIDX', 'OPTSTK')) = (opt_type IS NOT NULL AND strike_price IS NOT NULL AND expiry_date IS NOT NULL)");
                    table.CheckConstraint("ck_security_master_security_type", "security_type IN ('INDEX', 'EQUITY', 'FUTIDX', 'FUTSTK', 'OPTIDX', 'OPTSTK')");
                    table.CheckConstraint("ck_security_master_segment", "segment IN ('D', 'E')");
                    table.ForeignKey(
                        name: "fk_security_master_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_companies",
                schema: "mst",
                columns: table => new
                {
                    company_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    instrument_id = table.Column<long>(type: "bigint", nullable: true),
                    nse_symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sector = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    industry = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    is_fno_active = table.Column<bool>(type: "boolean", nullable: false),
                    nifty50_weight_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    banknifty_weight_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    finnifty_weight_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_companies", x => x.company_id);
                    table.CheckConstraint("ck_stock_companies_weights", "nifty50_weight_pct BETWEEN 0 AND 100 AND banknifty_weight_pct BETWEEN 0 AND 100 AND finnifty_weight_pct BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_stock_companies_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_auth_tokens",
                schema: "mst",
                columns: table => new
                {
                    token_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    x_jwt_token = table.Column<string>(type: "text", nullable: true),
                    public_access_token = table.Column<string>(type: "text", nullable: true),
                    read_access_token = table.Column<string>(type: "text", nullable: true),
                    token_expiry = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    login_status = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    login_method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_auth_tokens", x => x.token_id);
                    table.CheckConstraint("ck_account_auth_tokens_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_account_auth_tokens_login_method", "login_method IN ('HEADLESS', 'MANUAL')");
                    table.CheckConstraint("ck_account_auth_tokens_login_status", "login_status IN ('PENDING', 'SUCCESS', 'FAILED', 'EXPIRED')");
                    table.CheckConstraint("ck_account_auth_tokens_success_has_token", "login_status <> 'SUCCESS' OR (x_jwt_token IS NOT NULL AND token_expiry IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_account_auth_tokens_trading_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "mst",
                        principalTable: "trading_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_risk_config",
                schema: "mst",
                columns: table => new
                {
                    config_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    allocated_capital = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    opening_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    available_margin = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    utilised_margin = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    risk_per_trade_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    min_single_trade_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    max_single_trade_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    default_sl_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    default_target_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    min_risk_reward_ratio = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    max_sl_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    breakeven_trigger_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    trailing_sl_step_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    max_daily_loss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    max_daily_profit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    max_open_trades = table.Column<int>(type: "integer", nullable: false),
                    max_lots_per_trade = table.Column<int>(type: "integer", nullable: false),
                    lot_multiplier = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    min_pattern_confidence = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    block_trade_on_opposing_news = table.Column<bool>(type: "boolean", nullable: false),
                    slippage_buffer_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    simulated_slippage_pct = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    simulated_brokerage_per_order = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    funds_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_risk_config", x => x.config_id);
                    table.CheckConstraint("ck_account_risk_config_capital", "allocated_capital > 0 AND opening_balance >= 0 AND available_margin >= 0 AND utilised_margin >= 0");
                    table.CheckConstraint("ck_account_risk_config_confidence", "min_pattern_confidence BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_account_risk_config_costs", "slippage_buffer_pct BETWEEN 0 AND 5 AND simulated_slippage_pct BETWEEN 0 AND 5 AND simulated_brokerage_per_order >= 0");
                    table.CheckConstraint("ck_account_risk_config_daily_limits", "max_daily_loss > 0 AND max_daily_loss <= allocated_capital AND max_daily_profit > 0");
                    table.CheckConstraint("ck_account_risk_config_fallback_rr", "min_risk_reward_ratio >= 1 AND default_target_pct >= default_sl_pct * min_risk_reward_ratio");
                    table.CheckConstraint("ck_account_risk_config_premium_band", "min_single_trade_price > 0 AND max_single_trade_price > min_single_trade_price");
                    table.CheckConstraint("ck_account_risk_config_risk_pct", "risk_per_trade_pct > 0 AND risk_per_trade_pct <= 10");
                    table.CheckConstraint("ck_account_risk_config_sizing", "max_open_trades >= 1 AND max_lots_per_trade >= 1 AND lot_multiplier > 0");
                    table.CheckConstraint("ck_account_risk_config_sl_target", "default_sl_pct > 0 AND default_sl_pct < 100 AND default_target_pct > 0 AND max_sl_pct >= default_sl_pct AND max_sl_pct < 100");
                    table.CheckConstraint("ck_account_risk_config_trailing", "breakeven_trigger_pct > 0 AND breakeven_trigger_pct <= 100 AND trailing_sl_step_pct > 0 AND trailing_sl_step_pct < 100");
                    table.ForeignKey(
                        name: "fk_account_risk_config_trading_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "mst",
                        principalTable: "trading_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "daily_account_pnl",
                schema: "trn",
                columns: table => new
                {
                    summary_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    trade_mode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    opening_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    available_margin = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    utilised_margin = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_trades = table.Column<int>(type: "integer", nullable: false),
                    winning_trades = table.Column<int>(type: "integer", nullable: false),
                    losing_trades = table.Column<int>(type: "integer", nullable: false),
                    realized_gross_pnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    unrealized_mtm_pnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_charges = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    net_pnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, computedColumnSql: "realized_gross_pnl + unrealized_mtm_pnl - total_charges", stored: true),
                    daily_loss_limit_used_pct = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    kill_switch_triggered = table.Column<bool>(type: "boolean", nullable: false),
                    kill_switch_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    profit_lock_triggered = table.Column<bool>(type: "boolean", nullable: false),
                    halt_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_account_pnl", x => x.summary_id);
                    table.CheckConstraint("ck_daily_account_pnl_counts", "total_trades >= 0 AND winning_trades >= 0 AND losing_trades >= 0 AND winning_trades + losing_trades <= total_trades");
                    table.CheckConstraint("ck_daily_account_pnl_kill_switch", "NOT kill_switch_triggered OR kill_switch_at IS NOT NULL");
                    table.CheckConstraint("ck_daily_account_pnl_non_negative", "total_charges >= 0 AND daily_loss_limit_used_pct >= 0 AND opening_balance >= 0 AND available_margin >= 0 AND utilised_margin >= 0");
                    table.CheckConstraint("ck_daily_account_pnl_trade_mode", "trade_mode IN ('PAPER', 'REAL')");
                    table.ForeignKey(
                        name: "fk_daily_account_pnl_trading_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "mst",
                        principalTable: "trading_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "system_events",
                schema: "trn",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_events", x => x.event_id);
                    table.CheckConstraint("ck_system_events_severity", "severity IN ('INFO', 'WARNING', 'CRITICAL')");
                    table.ForeignKey(
                        name: "fk_system_events_trading_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "mst",
                        principalTable: "trading_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "detected_patterns",
                schema: "trn",
                columns: table => new
                {
                    detection_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    security_id = table.Column<long>(type: "bigint", nullable: true),
                    pattern_id = table.Column<long>(type: "bigint", nullable: false),
                    timeframe = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    trend_timeframe = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    trend_bias = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    direction = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    neck_or_breakout_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    structural_sl_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    structural_target_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pivot_coordinates = table.Column<string>(type: "jsonb", nullable: false),
                    confidence_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    is_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    breakout_candle_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_detected_patterns", x => x.detection_id);
                    table.CheckConstraint("ck_detected_patterns_confidence", "confidence_score BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_detected_patterns_direction", "direction IN ('BULLISH', 'BEARISH', 'NEUTRAL', 'BIDIRECTIONAL')");
                    table.CheckConstraint("ck_detected_patterns_directional", "direction IN ('BULLISH', 'BEARISH')");
                    table.CheckConstraint("ck_detected_patterns_structural_levels", "structural_sl_price > 0 AND structural_target_price > 0 AND neck_or_breakout_price > 0 AND ((direction = 'BULLISH' AND structural_sl_price < neck_or_breakout_price AND neck_or_breakout_price < structural_target_price) OR (direction = 'BEARISH' AND structural_target_price < neck_or_breakout_price AND neck_or_breakout_price < structural_sl_price))");
                    table.CheckConstraint("ck_detected_patterns_timeframe", "timeframe IN ('1m', '3m', '5m', '15m', '1h', '1d')");
                    table.CheckConstraint("ck_detected_patterns_trend_bias", "trend_bias IN ('BULLISH', 'BEARISH', 'NEUTRAL')");
                    table.CheckConstraint("ck_detected_patterns_trend_timeframe", "trend_timeframe IN ('1m', '3m', '5m', '15m', '1h', '1d')");
                    table.ForeignKey(
                        name: "fk_detected_patterns_chart_patterns_pattern_id",
                        column: x => x.pattern_id,
                        principalSchema: "mst",
                        principalTable: "chart_patterns",
                        principalColumn: "pattern_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_detected_patterns_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_detected_patterns_security_master_security_id",
                        column: x => x.security_id,
                        principalSchema: "mst",
                        principalTable: "security_master",
                        principalColumn: "security_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ohlcv_candles",
                schema: "trn",
                columns: table => new
                {
                    candle_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    security_id = table.Column<long>(type: "bigint", nullable: true),
                    timeframe = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    candle_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    high = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    low = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    close = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    volume = table.Column<long>(type: "bigint", nullable: false),
                    open_interest = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ohlcv_candles", x => x.candle_id);
                    table.CheckConstraint("ck_ohlcv_candles_range", "low >= 0 AND high >= low AND high >= GREATEST(open, close) AND low <= LEAST(open, close)");
                    table.CheckConstraint("ck_ohlcv_candles_timeframe", "timeframe IN ('1m', '3m', '5m', '15m', '1h', '1d')");
                    table.CheckConstraint("ck_ohlcv_candles_volume", "volume >= 0 AND (open_interest IS NULL OR open_interest >= 0)");
                    table.ForeignKey(
                        name: "fk_ohlcv_candles_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ohlcv_candles_security_master_security_id",
                        column: x => x.security_id,
                        principalSchema: "mst",
                        principalTable: "security_master",
                        principalColumn: "security_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "company_daily_news",
                schema: "trn",
                columns: table => new
                {
                    news_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    company_id = table.Column<long>(type: "bigint", nullable: true),
                    instrument_id = table.Column<long>(type: "bigint", nullable: true),
                    scan_type = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    headline = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    source_urls = table.Column<string>(type: "jsonb", nullable: false),
                    news_category = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    sentiment = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    sentiment_score = table.Column<int>(type: "integer", nullable: false),
                    impact_level = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    nifty_weighted_impact = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    banknifty_weighted_impact = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    gemini_structured_output = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    analyzed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_daily_news", x => x.news_id);
                    table.CheckConstraint("ck_company_daily_news_impact_level", "impact_level IN ('LOW', 'MEDIUM', 'HIGH', 'EXTREME_HALT')");
                    table.CheckConstraint("ck_company_daily_news_news_category", "news_category IN ('EARNINGS', 'CORPORATE_ACTION', 'REGULATORY', 'MERGER_ACQUISITION', 'MANAGEMENT', 'ORDER_WIN', 'RATING', 'LEGAL', 'SECTOR', 'MACRO', 'GLOBAL', 'OTHER')");
                    table.CheckConstraint("ck_company_daily_news_scan_type", "scan_type IN ('PREMARKET', 'INTRADAY_SPIKE', 'MANUAL')");
                    table.CheckConstraint("ck_company_daily_news_sentiment", "sentiment IN ('BULLISH', 'BEARISH', 'NEUTRAL')");
                    table.CheckConstraint("ck_company_daily_news_sentiment_score", "sentiment_score BETWEEN -100 AND 100");
                    table.ForeignKey(
                        name: "fk_company_daily_news_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_company_daily_news_stock_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "mst",
                        principalTable: "stock_companies",
                        principalColumn: "company_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "signals",
                schema: "trn",
                columns: table => new
                {
                    signal_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    detection_id = table.Column<long>(type: "bigint", nullable: false),
                    news_id = table.Column<long>(type: "bigint", nullable: true),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    security_id = table.Column<long>(type: "bigint", nullable: false),
                    signal_type = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    spot_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    option_delta = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: true),
                    entry_option_ltp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    mandatory_sl_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    mandatory_target_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    risk_reward_ratio = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false, computedColumnSql: "COALESCE(ROUND(CASE WHEN signal_type = 'BUY' THEN (mandatory_target_price - entry_option_ltp) / NULLIF(entry_option_ltp - mandatory_sl_price, 0) ELSE (entry_option_ltp - mandatory_target_price) / NULLIF(mandatory_sl_price - entry_option_ltp, 0) END, 4), 0)", stored: true),
                    sl_method = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    pattern_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    news_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    oi_greeks_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    combined_ai_confidence = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    greeks_and_oi = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signals", x => x.signal_id);
                    table.CheckConstraint("ck_signals_mandatory_sl_target", "entry_option_ltp > 0 AND mandatory_sl_price > 0 AND mandatory_target_price > 0 AND ((signal_type = 'BUY' AND mandatory_sl_price < entry_option_ltp AND entry_option_ltp < mandatory_target_price) OR (signal_type = 'SELL' AND mandatory_target_price < entry_option_ltp AND entry_option_ltp < mandatory_sl_price))");
                    table.CheckConstraint("ck_signals_rejection_reason", "status <> 'REJECTED' OR rejection_reason IS NOT NULL");
                    table.CheckConstraint("ck_signals_risk_reward_positive", "risk_reward_ratio > 0");
                    table.CheckConstraint("ck_signals_scores", "pattern_score BETWEEN 0 AND 100 AND news_score BETWEEN 0 AND 100 AND oi_greeks_score BETWEEN 0 AND 100 AND combined_ai_confidence BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_signals_signal_type", "signal_type IN ('BUY', 'SELL')");
                    table.CheckConstraint("ck_signals_sl_method", "sl_method IN ('STRUCTURAL', 'FIXED_PCT_FALLBACK')");
                    table.CheckConstraint("ck_signals_status", "status IN ('NEW', 'DISPATCHED', 'REJECTED', 'EXPIRED')");
                    table.ForeignKey(
                        name: "fk_signals_company_daily_news_news_id",
                        column: x => x.news_id,
                        principalSchema: "trn",
                        principalTable: "company_daily_news",
                        principalColumn: "news_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_signals_detected_patterns_detection_id",
                        column: x => x.detection_id,
                        principalSchema: "trn",
                        principalTable: "detected_patterns",
                        principalColumn: "detection_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_signals_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_signals_security_master_security_id",
                        column: x => x.security_id,
                        principalSchema: "mst",
                        principalTable: "security_master",
                        principalColumn: "security_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "trn",
                columns: table => new
                {
                    order_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    signal_id = table.Column<long>(type: "bigint", nullable: false),
                    parent_order_id = table.Column<long>(type: "bigint", nullable: true),
                    order_leg_type = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    trade_mode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_order_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    exchange_order_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    txn_type = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    segment = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    product = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    security_id = table.Column<long>(type: "bigint", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    filled_quantity = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    trigger_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    stop_loss_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    target_sell_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    order_ltp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    avg_fill_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    order_type = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    raw_response = table.Column<string>(type: "jsonb", nullable: true),
                    placed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    filled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.order_id);
                    table.CheckConstraint("ck_orders_derivatives_only", "segment = 'D'");
                    table.CheckConstraint("ck_orders_entry_bracket", "order_leg_type <> 'ENTRY' OR (stop_loss_price < order_ltp AND order_ltp < target_sell_price AND stop_loss_price < price AND price < target_sell_price)");
                    table.CheckConstraint("ck_orders_leg_order_type", "(order_leg_type <> 'STOP_LOSS' OR order_type = 'SL') AND (order_leg_type <> 'TARGET_SELL' OR order_type = 'LMT')");
                    table.CheckConstraint("ck_orders_long_only", "(order_leg_type = 'ENTRY') = (txn_type = 'B')");
                    table.CheckConstraint("ck_orders_mandatory_sl_target", "stop_loss_price > 0 AND target_sell_price > stop_loss_price");
                    table.CheckConstraint("ck_orders_order_leg_type", "order_leg_type IN ('ENTRY', 'STOP_LOSS', 'TARGET_SELL', 'EMERGENCY_EXIT')");
                    table.CheckConstraint("ck_orders_order_type", "order_type IN ('LMT', 'SL')");
                    table.CheckConstraint("ck_orders_parent_link", "(order_leg_type = 'ENTRY') = (parent_order_id IS NULL)");
                    table.CheckConstraint("ck_orders_prices", "price > 0 AND order_ltp > 0 AND (avg_fill_price IS NULL OR avg_fill_price > 0)");
                    table.CheckConstraint("ck_orders_product", "product IN ('I', 'M', 'B')");
                    table.CheckConstraint("ck_orders_quantity", "quantity > 0 AND filled_quantity >= 0 AND filled_quantity <= quantity");
                    table.CheckConstraint("ck_orders_rejection_reason", "status NOT IN ('REJECTED', 'FAILED') OR rejection_reason IS NOT NULL");
                    table.CheckConstraint("ck_orders_segment", "segment IN ('D', 'E')");
                    table.CheckConstraint("ck_orders_sl_trigger", "order_type <> 'SL' OR (trigger_price IS NOT NULL AND trigger_price > 0 AND (txn_type <> 'S' OR price <= trigger_price) AND (txn_type <> 'B' OR price >= trigger_price))");
                    table.CheckConstraint("ck_orders_status", "status IN ('PENDING', 'PLACED', 'OPEN', 'TRIGGER_PENDING', 'PARTIALLY_FILLED', 'FILLED', 'CANCELLED', 'REJECTED', 'FAILED')");
                    table.CheckConstraint("ck_orders_trade_mode", "trade_mode IN ('PAPER', 'REAL')");
                    table.CheckConstraint("ck_orders_txn_type", "txn_type IN ('B', 'S')");
                    table.ForeignKey(
                        name: "fk_orders_orders_parent_order_id",
                        column: x => x.parent_order_id,
                        principalSchema: "trn",
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_security_master_security_id",
                        column: x => x.security_id,
                        principalSchema: "mst",
                        principalTable: "security_master",
                        principalColumn: "security_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_signals_signal_id",
                        column: x => x.signal_id,
                        principalSchema: "trn",
                        principalTable: "signals",
                        principalColumn: "signal_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_trading_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "mst",
                        principalTable: "trading_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "daily_trades",
                schema: "trn",
                columns: table => new
                {
                    trade_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    trade_mode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    signal_id = table.Column<long>(type: "bigint", nullable: false),
                    security_id = table.Column<long>(type: "bigint", nullable: false),
                    detection_id = table.Column<long>(type: "bigint", nullable: false),
                    news_id = table.Column<long>(type: "bigint", nullable: true),
                    entry_order_id = table.Column<long>(type: "bigint", nullable: false),
                    sl_order_id = table.Column<long>(type: "bigint", nullable: true),
                    target_order_id = table.Column<long>(type: "bigint", nullable: true),
                    exit_order_id = table.Column<long>(type: "bigint", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    lots = table.Column<int>(type: "integer", nullable: false),
                    entry_buy_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    stop_loss_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    trailing_sl_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    target_sell_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    peak_ltp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    current_ltp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    exit_sell_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    trade_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    gross_pnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    brokerage_and_charges = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    net_pnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, computedColumnSql: "gross_pnl - brokerage_and_charges", stored: true),
                    entry_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    exit_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_trades", x => x.trade_id);
                    table.CheckConstraint("ck_daily_trades_charges", "brokerage_and_charges >= 0");
                    table.CheckConstraint("ck_daily_trades_exit_state", "(trade_status = 'OPEN') = (exit_time IS NULL AND exit_sell_price IS NULL)");
                    table.CheckConstraint("ck_daily_trades_mandatory_sl_target", "entry_buy_price > 0 AND stop_loss_price > 0 AND stop_loss_price < entry_buy_price AND target_sell_price > entry_buy_price");
                    table.CheckConstraint("ck_daily_trades_quantity", "quantity > 0 AND lots > 0");
                    table.CheckConstraint("ck_daily_trades_trade_mode", "trade_mode IN ('PAPER', 'REAL')");
                    table.CheckConstraint("ck_daily_trades_trade_status", "trade_status IN ('OPEN', 'TARGET_HIT', 'SL_HIT', 'TRAILING_SL_HIT', 'TIME_SQUARE_OFF', 'KILL_SWITCH_EXIT', 'MANUAL_EXIT')");
                    table.CheckConstraint("ck_daily_trades_trailing_sl", "trailing_sl_price >= stop_loss_price AND trailing_sl_price < target_sell_price");
                    table.ForeignKey(
                        name: "fk_daily_trades_company_daily_news_news_id",
                        column: x => x.news_id,
                        principalSchema: "trn",
                        principalTable: "company_daily_news",
                        principalColumn: "news_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_detected_patterns_detection_id",
                        column: x => x.detection_id,
                        principalSchema: "trn",
                        principalTable: "detected_patterns",
                        principalColumn: "detection_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_orders_entry_order_id",
                        column: x => x.entry_order_id,
                        principalSchema: "trn",
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_orders_exit_order_id",
                        column: x => x.exit_order_id,
                        principalSchema: "trn",
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_orders_sl_order_id",
                        column: x => x.sl_order_id,
                        principalSchema: "trn",
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_orders_target_order_id",
                        column: x => x.target_order_id,
                        principalSchema: "trn",
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_security_master_security_id",
                        column: x => x.security_id,
                        principalSchema: "mst",
                        principalTable: "security_master",
                        principalColumn: "security_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_signals_signal_id",
                        column: x => x.signal_id,
                        principalSchema: "trn",
                        principalTable: "signals",
                        principalColumn: "signal_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_daily_trades_trading_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "mst",
                        principalTable: "trading_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_auth_tokens_account_id_created_at",
                schema: "mst",
                table: "account_auth_tokens",
                columns: new[] { "account_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_account_risk_config_account_id",
                schema: "mst",
                table: "account_risk_config",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alert_channels_channel_name",
                schema: "mst",
                table: "alert_channels",
                column: "channel_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_charge_rates_security_type_effective_from",
                schema: "mst",
                table: "charge_rates",
                columns: new[] { "security_type", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chart_patterns_category_is_active",
                schema: "mst",
                table: "chart_patterns",
                columns: new[] { "category", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_chart_patterns_pattern_code",
                schema: "mst",
                table: "chart_patterns",
                column: "pattern_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_daily_news_company_id",
                schema: "trn",
                table: "company_daily_news",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_company_daily_news_instrument_id",
                schema: "trn",
                table: "company_daily_news",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_company_daily_news_trading_date_company_id",
                schema: "trn",
                table: "company_daily_news",
                columns: new[] { "trading_date", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_company_daily_news_trading_date_content_hash",
                schema: "trn",
                table: "company_daily_news",
                columns: new[] { "trading_date", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_daily_news_trading_date_impact_level",
                schema: "trn",
                table: "company_daily_news",
                columns: new[] { "trading_date", "impact_level" });

            migrationBuilder.CreateIndex(
                name: "ix_company_daily_news_trading_date_instrument_id",
                schema: "trn",
                table: "company_daily_news",
                columns: new[] { "trading_date", "instrument_id" });

            migrationBuilder.CreateIndex(
                name: "ix_daily_account_pnl_account_id",
                schema: "trn",
                table: "daily_account_pnl",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_account_pnl_trading_date_account_id",
                schema: "trn",
                table: "daily_account_pnl",
                columns: new[] { "trading_date", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_market_sentiment_instrument_id",
                schema: "trn",
                table: "daily_market_sentiment",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_market_sentiment_trading_date_instrument_id",
                schema: "trn",
                table: "daily_market_sentiment",
                columns: new[] { "trading_date", "instrument_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_detection_id",
                schema: "trn",
                table: "daily_trades",
                column: "detection_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_entry_order_id",
                schema: "trn",
                table: "daily_trades",
                column: "entry_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_exit_order_id",
                schema: "trn",
                table: "daily_trades",
                column: "exit_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_news_id",
                schema: "trn",
                table: "daily_trades",
                column: "news_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_open",
                schema: "trn",
                table: "daily_trades",
                column: "account_id",
                filter: "trade_status = 'OPEN'");

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_security_id",
                schema: "trn",
                table: "daily_trades",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_signal_id",
                schema: "trn",
                table: "daily_trades",
                column: "signal_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_sl_order_id",
                schema: "trn",
                table: "daily_trades",
                column: "sl_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_target_order_id",
                schema: "trn",
                table: "daily_trades",
                column: "target_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_trades_trading_date_account_id",
                schema: "trn",
                table: "daily_trades",
                columns: new[] { "trading_date", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_detected_patterns_instrument_id_detected_at",
                schema: "trn",
                table: "detected_patterns",
                columns: new[] { "instrument_id", "detected_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_detected_patterns_pattern_id",
                schema: "trn",
                table: "detected_patterns",
                column: "pattern_id");

            migrationBuilder.CreateIndex(
                name: "ix_detected_patterns_security_id",
                schema: "trn",
                table: "detected_patterns",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ux_gemini_news_config_single_active",
                schema: "mst",
                table: "gemini_news_config",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_is_tradable",
                schema: "mst",
                table: "instruments",
                column: "is_tradable",
                filter: "is_tradable AND is_active");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_symbol",
                schema: "mst",
                table: "instruments",
                column: "symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ohlcv_candles_option_series",
                schema: "trn",
                table: "ohlcv_candles",
                columns: new[] { "security_id", "timeframe", "candle_time" },
                filter: "security_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ohlcv_candles_series_time",
                schema: "trn",
                table: "ohlcv_candles",
                columns: new[] { "instrument_id", "security_id", "timeframe", "candle_time" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_orders_account_id_broker_order_no",
                schema: "trn",
                table: "orders",
                columns: new[] { "account_id", "broker_order_no" },
                unique: true,
                filter: "broker_order_no IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_idempotency_key",
                schema: "trn",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_parent_order_id",
                schema: "trn",
                table: "orders",
                column: "parent_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_security_id",
                schema: "trn",
                table: "orders",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_signal_id",
                schema: "trn",
                table: "orders",
                column: "signal_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_trading_date_account_id",
                schema: "trn",
                table: "orders",
                columns: new[] { "trading_date", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_working",
                schema: "trn",
                table: "orders",
                columns: new[] { "account_id", "status" },
                filter: "status IN ('PENDING', 'PLACED', 'OPEN', 'TRIGGER_PENDING', 'PARTIALLY_FILLED')");

            migrationBuilder.CreateIndex(
                name: "ix_security_master_option_chain",
                schema: "mst",
                table: "security_master",
                columns: new[] { "instrument_id", "expiry_date", "strike_price", "opt_type" });

            migrationBuilder.CreateIndex(
                name: "ix_security_master_pml_symbol",
                schema: "mst",
                table: "security_master",
                column: "pml_symbol");

            migrationBuilder.CreateIndex(
                name: "ix_signals_detection_id",
                schema: "trn",
                table: "signals",
                column: "detection_id");

            migrationBuilder.CreateIndex(
                name: "ix_signals_instrument_id",
                schema: "trn",
                table: "signals",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_signals_news_id",
                schema: "trn",
                table: "signals",
                column: "news_id");

            migrationBuilder.CreateIndex(
                name: "ix_signals_security_id",
                schema: "trn",
                table: "signals",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ix_signals_trading_date_status",
                schema: "trn",
                table: "signals",
                columns: new[] { "trading_date", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_companies_instrument_id",
                schema: "mst",
                table: "stock_companies",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_companies_nse_symbol",
                schema: "mst",
                table: "stock_companies",
                column: "nse_symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_strategy_config_single_active",
                schema: "mst",
                table: "strategy_config",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_account_id_created_at",
                schema: "trn",
                table: "system_events",
                columns: new[] { "account_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_system_events_created_at",
                schema: "trn",
                table: "system_events",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_trading_accounts_client_id",
                schema: "mst",
                table: "trading_accounts",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_trading_accounts_single_data_provider",
                schema: "mst",
                table: "trading_accounts",
                column: "is_data_provider",
                unique: true,
                filter: "is_data_provider AND is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_auth_tokens",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "account_risk_config",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "alert_channels",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "charge_rates",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "daily_account_pnl",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "daily_bot_state",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "daily_market_sentiment",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "daily_trades",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "gemini_news_config",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "ohlcv_candles",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "strategy_config",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "system_events",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "signals",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "trading_accounts",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "company_daily_news",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "detected_patterns",
                schema: "trn");

            migrationBuilder.DropTable(
                name: "stock_companies",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "chart_patterns",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "security_master",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "instruments",
                schema: "mst");
        }
    }
}
