using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.RenameColumn(
                name: "notification_type",
                schema: "semantico",
                table: "subscriptions",
                newName: "recipient_id");

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

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_recipient_id",
                schema: "semantico",
                table: "subscriptions",
                column: "recipient_id");

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_recipients_recipient_id",
                schema: "semantico",
                table: "subscriptions",
                column: "recipient_id",
                principalSchema: "semantico",
                principalTable: "recipients",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_recipients_recipient_id",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropTable(
                name: "recipients",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_recipient_id",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.RenameColumn(
                name: "recipient_id",
                schema: "semantico",
                table: "subscriptions",
                newName: "notification_type");

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
