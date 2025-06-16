using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipientsToQueryExecutionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "query_execution_history_recipient",
                schema: "semantico");
        }
    }
}
