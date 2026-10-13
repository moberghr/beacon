using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
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
                name: "ContentHash",
                schema: "beacon",
                table: "QueryVersions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedContentHash",
                schema: "beacon",
                table: "QueryApprovalRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BaseVersionId",
                schema: "beacon",
                table: "QueryApprovalRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContributorUserIds",
                schema: "beacon",
                table: "QueryApprovalRequests",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentHash",
                schema: "beacon",
                table: "QueryVersions");

            migrationBuilder.DropColumn(
                name: "ApprovedContentHash",
                schema: "beacon",
                table: "QueryApprovalRequests");

            migrationBuilder.DropColumn(
                name: "BaseVersionId",
                schema: "beacon",
                table: "QueryApprovalRequests");

            migrationBuilder.DropColumn(
                name: "ContributorUserIds",
                schema: "beacon",
                table: "QueryApprovalRequests");
        }
    }
}
