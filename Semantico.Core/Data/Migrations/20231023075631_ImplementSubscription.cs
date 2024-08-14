using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.Migrations
{
    /// <inheritdoc />
    public partial class ImplementSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "semantico");

            migrationBuilder.DropColumn(
                name: "cron_expression",
                schema: "semantico",
                table: "queries");

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_time",
                schema: "semantico",
                table: "queries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_time",
                schema: "semantico",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_time",
                schema: "semantico",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    cron_expression = table.Column<string>(type: "text", nullable: false),
                    recipient = table.Column<string>(type: "text", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                schema: "semantico",
                table: "accounts",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "archived_time", "created_time" },
                values: new object[] { null, new DateTime(2023, 2, 2, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_query_id",
                schema: "semantico",
                table: "subscriptions",
                column: "query_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "semantico");

            migrationBuilder.DropColumn(
                name: "archived_time",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "archived_time",
                schema: "semantico",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "archived_time",
                schema: "semantico",
                table: "accounts");

            migrationBuilder.AddColumn<string>(
                name: "cron_expression",
                schema: "semantico",
                table: "queries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                schema: "semantico",
                table: "accounts",
                keyColumn: "id",
                keyValue: 1,
                column: "created_time",
                value: new DateTime(2023, 7, 11, 10, 48, 23, 809, DateTimeKind.Utc).AddTicks(5305));

            migrationBuilder.CreateIndex(
                name: "ix_notifications_query_id",
                schema: "semantico",
                table: "notifications",
                column: "query_id");
        }
    }
}
