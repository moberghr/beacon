using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroundingAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No backfill: golden cases created before keep no promoting or editing user, and verdicts
            // recorded before keep no rating user.
            migrationBuilder.AddColumn<int>(
                name: "promoted_by_user_id",
                schema: "beacon",
                table: "mcp_eval_cases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "modified_by_user_id",
                schema: "beacon",
                table: "mcp_eval_cases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "feedback_by_user_id",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "integer",
                nullable: true);

            // A promotion claims its signal by setting promoted_at once. Signals promoted before keep both columns null
            // (their golden cases are found by source_signal_id); no stored value changes.
            migrationBuilder.AddColumn<DateTime>(
                name: "promoted_at",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "promoted_eval_case_id",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "promoted_at",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "promoted_eval_case_id",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "promoted_by_user_id",
                schema: "beacon",
                table: "mcp_eval_cases");

            migrationBuilder.DropColumn(
                name: "modified_by_user_id",
                schema: "beacon",
                table: "mcp_eval_cases");

            migrationBuilder.DropColumn(
                name: "feedback_by_user_id",
                schema: "beacon",
                table: "mcp_query_signals");
        }
    }
}
