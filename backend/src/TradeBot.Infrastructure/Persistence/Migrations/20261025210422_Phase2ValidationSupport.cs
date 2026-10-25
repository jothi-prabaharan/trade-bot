using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2ValidationSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_strategy_config_timeframes",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropCheckConstraint(
                name: "ck_account_risk_config_sizing",
                schema: "mst",
                table: "account_risk_config");

            migrationBuilder.AddColumn<int>(
                name: "freeze_quantity",
                schema: "mst",
                table: "instruments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Conservative backfill (1 lot) so the CHECK holds; real freeze limits come from the seed/admin
            migrationBuilder.Sql("UPDATE mst.instruments SET freeze_quantity = lot_size WHERE freeze_quantity = 0;");

            migrationBuilder.CreateTable(
                name: "market_holidays",
                schema: "mst",
                columns: table => new
                {
                    holiday_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    holiday_date = table.Column<DateOnly>(type: "date", nullable: false),
                    exchange = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_special_session = table.Column<bool>(type: "boolean", nullable: false),
                    session_open_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    session_close_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_market_holidays", x => x.holiday_id);
                    table.CheckConstraint("ck_market_holidays_exchange", "exchange IN ('NSE', 'BSE')");
                    table.CheckConstraint("ck_market_holidays_special_session", "(is_special_session AND session_open_time IS NOT NULL AND session_close_time IS NOT NULL AND session_open_time < session_close_time) OR (NOT is_special_session AND session_open_time IS NULL AND session_close_time IS NULL)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_strategy_config_timeframes",
                schema: "mst",
                table: "strategy_config",
                sql: "array_position(ARRAY['1m','3m','5m','15m','1h','1d'], trend_timeframe::text) > array_position(ARRAY['1m','3m','5m','15m','1h','1d'], trigger_timeframe::text)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_instruments_freeze_quantity",
                schema: "mst",
                table: "instruments",
                sql: "freeze_quantity >= lot_size");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gemini_news_config_premarket_time",
                schema: "mst",
                table: "gemini_news_config",
                sql: "premarket_scan_time < TIME '09:15'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_risk_config_sizing",
                schema: "mst",
                table: "account_risk_config",
                sql: "max_open_trades >= 1 AND max_lots_per_trade >= 1 AND lot_multiplier > 0 AND lot_multiplier <= 1");

            migrationBuilder.CreateIndex(
                name: "ix_market_holidays_exchange_holiday_date",
                schema: "mst",
                table: "market_holidays",
                columns: new[] { "exchange", "holiday_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_holidays",
                schema: "mst");

            migrationBuilder.DropCheckConstraint(
                name: "ck_strategy_config_timeframes",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropCheckConstraint(
                name: "ck_instruments_freeze_quantity",
                schema: "mst",
                table: "instruments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gemini_news_config_premarket_time",
                schema: "mst",
                table: "gemini_news_config");

            migrationBuilder.DropCheckConstraint(
                name: "ck_account_risk_config_sizing",
                schema: "mst",
                table: "account_risk_config");

            migrationBuilder.DropColumn(
                name: "freeze_quantity",
                schema: "mst",
                table: "instruments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_strategy_config_timeframes",
                schema: "mst",
                table: "strategy_config",
                sql: "trend_timeframe <> trigger_timeframe");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_risk_config_sizing",
                schema: "mst",
                table: "account_risk_config",
                sql: "max_open_trades >= 1 AND max_lots_per_trade >= 1 AND lot_multiplier > 0");
        }
    }
}
