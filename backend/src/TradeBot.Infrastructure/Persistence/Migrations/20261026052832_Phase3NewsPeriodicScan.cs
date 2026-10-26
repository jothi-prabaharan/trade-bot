using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3NewsPeriodicScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_company_daily_news_scan_type",
                schema: "trn",
                table: "company_daily_news");

            migrationBuilder.AddCheckConstraint(
                name: "ck_company_daily_news_scan_type",
                schema: "trn",
                table: "company_daily_news",
                sql: "scan_type IN ('PREMARKET', 'INTRADAY_SPIKE', 'MANUAL', 'PERIODIC')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_company_daily_news_scan_type",
                schema: "trn",
                table: "company_daily_news");

            migrationBuilder.AddCheckConstraint(
                name: "ck_company_daily_news_scan_type",
                schema: "trn",
                table: "company_daily_news",
                sql: "scan_type IN ('PREMARKET', 'INTRADAY_SPIKE', 'MANUAL')");
        }
    }
}
