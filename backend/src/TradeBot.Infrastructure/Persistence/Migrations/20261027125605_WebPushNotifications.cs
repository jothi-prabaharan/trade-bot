using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebPushNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "web_push_keys",
                schema: "mst",
                columns: table => new
                {
                    web_push_keys_id = table.Column<int>(type: "integer", nullable: false),
                    public_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    private_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_web_push_keys", x => x.web_push_keys_id);
                    table.CheckConstraint("ck_web_push_keys_single_row", "web_push_keys_id = 1");
                });

            migrationBuilder.CreateTable(
                name: "web_push_subscriptions",
                schema: "mst",
                columns: table => new
                {
                    web_push_subscription_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    p256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    auth = table.Column<string>(type: "text", nullable: false),
                    device_label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    notify_on_entry = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_exit = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_critical = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_info = table.Column<bool>(type: "boolean", nullable: false),
                    last_success_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_web_push_subscriptions", x => x.web_push_subscription_id);
                    table.CheckConstraint("ck_web_push_subscriptions_https", "endpoint LIKE 'https://%'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_web_push_subscriptions_endpoint",
                schema: "mst",
                table: "web_push_subscriptions",
                column: "endpoint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "web_push_keys",
                schema: "mst");

            migrationBuilder.DropTable(
                name: "web_push_subscriptions",
                schema: "mst");
        }
    }
}
