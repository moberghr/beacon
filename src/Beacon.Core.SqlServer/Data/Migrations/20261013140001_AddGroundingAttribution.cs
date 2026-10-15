using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
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
                name: "PromotedByUserId",
                schema: "beacon",
                table: "McpEvalCases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModifiedByUserId",
                schema: "beacon",
                table: "McpEvalCases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeedbackByUserId",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "int",
                nullable: true);

            // A promotion claims its signal by setting PromotedAt once. Signals promoted before keep both columns null
            // (their golden cases are found by SourceSignalId); no stored value changes.
            migrationBuilder.AddColumn<DateTime>(
                name: "PromotedAt",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromotedEvalCaseId",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromotedAt",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "PromotedEvalCaseId",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "PromotedByUserId",
                schema: "beacon",
                table: "McpEvalCases");

            migrationBuilder.DropColumn(
                name: "ModifiedByUserId",
                schema: "beacon",
                table: "McpEvalCases");

            migrationBuilder.DropColumn(
                name: "FeedbackByUserId",
                schema: "beacon",
                table: "McpQuerySignals");
        }
    }
}
