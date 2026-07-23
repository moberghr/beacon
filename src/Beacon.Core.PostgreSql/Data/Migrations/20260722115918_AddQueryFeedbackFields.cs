using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryFeedbackFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "feedback_note",
                table: "mcp_query_signals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_corrected_sql",
                table: "mcp_query_signals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "user_verdict",
                table: "mcp_query_signals",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "feedback_note",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "user_corrected_sql",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "user_verdict",
                table: "mcp_query_signals");
        }
    }
}
