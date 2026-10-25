using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3TradePacingAndWatchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "entry_fill_timeout_seconds",
                schema: "mst",
                table: "strategy_config",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "history_backfill_days",
                schema: "mst",
                table: "strategy_config",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "sl_gap_fallback_seconds",
                schema: "mst",
                table: "strategy_config",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "sl_limit_buffer_pct",
                schema: "mst",
                table: "strategy_config",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "stock_watchlist_size",
                schema: "mst",
                table: "strategy_config",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "max_trades_per_day",
                schema: "mst",
                table: "account_risk_config",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reentry_cooldown_minutes",
                schema: "mst",
                table: "account_risk_config",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "daily_watchlist",
                schema: "trn",
                columns: table => new
                {
                    watchlist_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'Asia/Kolkata')::date"),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    score = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_watchlist", x => x.watchlist_id);
                    table.CheckConstraint("ck_daily_watchlist_source", "source IN ('INDEX', 'PREMARKET', 'VOLUME_SPIKE', 'NEWS')");
                    table.ForeignKey(
                        name: "fk_daily_watchlist_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "mst",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_strategy_config_execution",
                schema: "mst",
                table: "strategy_config",
                sql: "sl_limit_buffer_pct > 0 AND sl_limit_buffer_pct <= 10 AND sl_gap_fallback_seconds BETWEEN 1 AND 30 AND entry_fill_timeout_seconds BETWEEN 2 AND 60");

            migrationBuilder.AddCheckConstraint(
                name: "ck_strategy_config_universe",
                schema: "mst",
                table: "strategy_config",
                sql: "stock_watchlist_size BETWEEN 0 AND 50 AND history_backfill_days BETWEEN 1 AND 30");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_risk_config_trade_pacing",
                schema: "mst",
                table: "account_risk_config",
                sql: "max_trades_per_day BETWEEN 1 AND 100 AND reentry_cooldown_minutes BETWEEN 0 AND 240");

            migrationBuilder.CreateIndex(
                name: "ix_daily_watchlist_instrument_id",
                schema: "trn",
                table: "daily_watchlist",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_watchlist_trading_date_instrument_id",
                schema: "trn",
                table: "daily_watchlist",
                columns: new[] { "trading_date", "instrument_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_watchlist",
                schema: "trn");

            migrationBuilder.DropCheckConstraint(
                name: "ck_strategy_config_execution",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropCheckConstraint(
                name: "ck_strategy_config_universe",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropCheckConstraint(
                name: "ck_account_risk_config_trade_pacing",
                schema: "mst",
                table: "account_risk_config");

            migrationBuilder.DropColumn(
                name: "entry_fill_timeout_seconds",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropColumn(
                name: "history_backfill_days",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropColumn(
                name: "sl_gap_fallback_seconds",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropColumn(
                name: "sl_limit_buffer_pct",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropColumn(
                name: "stock_watchlist_size",
                schema: "mst",
                table: "strategy_config");

            migrationBuilder.DropColumn(
                name: "max_trades_per_day",
                schema: "mst",
                table: "account_risk_config");

            migrationBuilder.DropColumn(
                name: "reentry_cooldown_minutes",
                schema: "mst",
                table: "account_risk_config");
        }
    }
}
