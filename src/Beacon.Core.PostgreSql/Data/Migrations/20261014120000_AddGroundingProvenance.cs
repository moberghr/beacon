using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
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
                name: "feedback_from_curator",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "candidate_sql_hash",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                schema: "beacon",
                table: "mcp_learned_patterns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "feedback_from_curator",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "candidate_sql_hash",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "beacon",
                table: "mcp_learned_patterns");
        }
    }
}
