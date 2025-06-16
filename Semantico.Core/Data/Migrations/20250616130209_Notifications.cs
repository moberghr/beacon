using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "query_execution_history_recipient",
                schema: "semantico");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_execution_history_id = table.Column<int>(type: "integer", nullable: false),
                    recipient_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_query_execution_history_query_execution_histo",
                        column: x => x.query_execution_history_id,
                        principalSchema: "semantico",
                        principalTable: "query_execution_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_recipients_recipient_id",
                        column: x => x.recipient_id,
                        principalSchema: "semantico",
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_query_execution_history_id",
                schema: "semantico",
                table: "notifications",
                column: "query_execution_history_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_id",
                schema: "semantico",
                table: "notifications",
                column: "recipient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "semantico");

            migrationBuilder.CreateTable(
                name: "query_execution_history_recipient",
                schema: "semantico",
                columns: table => new
                {
                    query_execution_histories_id = table.Column<int>(type: "integer", nullable: false),
                    recipients_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_execution_history_recipient", x => new { x.query_execution_histories_id, x.recipients_id });
                    table.ForeignKey(
                        name: "fk_query_execution_history_recipient_query_execution_history_q",
                        column: x => x.query_execution_histories_id,
                        principalSchema: "semantico",
                        principalTable: "query_execution_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_query_execution_history_recipient_recipients_recipients_id",
                        column: x => x.recipients_id,
                        principalSchema: "semantico",
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_recipient_recipients_id",
                schema: "semantico",
                table: "query_execution_history_recipient",
                column: "recipients_id");
        }
    }
}
