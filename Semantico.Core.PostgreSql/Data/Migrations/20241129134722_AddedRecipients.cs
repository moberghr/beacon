using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "notification_type",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "recipient",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "notification_type",
                schema: "semantico",
                table: "query_execution_history");

            migrationBuilder.DropColumn(
                name: "recipient",
                schema: "semantico",
                table: "query_execution_history");

            migrationBuilder.CreateTable(
                name: "recipients",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    destination = table.Column<string>(type: "text", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recipient_subscription",
                schema: "semantico",
                columns: table => new
                {
                    recipients_id = table.Column<int>(type: "integer", nullable: false),
                    subscriptions_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipient_subscription", x => new { x.recipients_id, x.subscriptions_id });
                    table.ForeignKey(
                        name: "fk_recipient_subscription_recipients_recipients_id",
                        column: x => x.recipients_id,
                        principalSchema: "semantico",
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recipient_subscription_subscriptions_subscriptions_id",
                        column: x => x.subscriptions_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recipient_subscription_subscriptions_id",
                schema: "semantico",
                table: "recipient_subscription",
                column: "subscriptions_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recipient_subscription",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "recipients",
                schema: "semantico");

            migrationBuilder.AddColumn<int>(
                name: "notification_type",
                schema: "semantico",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "recipient",
                schema: "semantico",
                table: "subscriptions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "notification_type",
                schema: "semantico",
                table: "query_execution_history",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "recipient",
                schema: "semantico",
                table: "query_execution_history",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
