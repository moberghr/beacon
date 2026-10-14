using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroundingProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only, no stored value is rewritten. Verdicts recorded before read as not a curator's (false): their
            // provenance was not recorded. The digests stay empty for existing rows; Beacon stores a row's digest in
            // application code the first time a decision, an auto-approval or a promotion touches it.
            migrationBuilder.AddColumn<bool>(
                name: "FeedbackFromCurator",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CandidateSqlHash",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                schema: "beacon",
                table: "McpLearnedPatterns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedbackFromCurator",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "CandidateSqlHash",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                schema: "beacon",
                table: "McpLearnedPatterns");
        }
    }
}
