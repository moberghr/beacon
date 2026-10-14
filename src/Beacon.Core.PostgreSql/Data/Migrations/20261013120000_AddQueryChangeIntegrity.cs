using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryChangeIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: versions recorded before keep no content hash (Beacon computes it from their
            // snapshot when it needs one), and requests recorded before keep no base version, no approved hash and no
            // contributors.
            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                schema: "beacon",
                table: "query_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_content_hash",
                schema: "beacon",
                table: "query_approval_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "base_version_id",
                schema: "beacon",
                table: "query_approval_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "contributor_user_ids",
                schema: "beacon",
                table: "query_approval_requests",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "beacon",
                table: "query_versions");

            migrationBuilder.DropColumn(
                name: "approved_content_hash",
                schema: "beacon",
                table: "query_approval_requests");

            migrationBuilder.DropColumn(
                name: "base_version_id",
                schema: "beacon",
                table: "query_approval_requests");

            migrationBuilder.DropColumn(
                name: "contributor_user_ids",
                schema: "beacon",
                table: "query_approval_requests");
        }
    }
}
