using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionChangeApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedTargetHash",
                schema: "beacon",
                table: "DataSources",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedPayload",
                schema: "beacon",
                table: "ChangeApprovalRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeApprovalRequests_Kind_ResourceId",
                schema: "beacon",
                table: "ChangeApprovalRequests",
                columns: new[] { "Kind", "ResourceId" },
                unique: true,
                filter: "[Kind] = 5 AND [Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChangeApprovalRequests_Kind_ResourceId",
                schema: "beacon",
                table: "ChangeApprovalRequests");

            migrationBuilder.DropColumn(
                name: "ApprovedTargetHash",
                schema: "beacon",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "EncryptedPayload",
                schema: "beacon",
                table: "ChangeApprovalRequests");
        }
    }
}
