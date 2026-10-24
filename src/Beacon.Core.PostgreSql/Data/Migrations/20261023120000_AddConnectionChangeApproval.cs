using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionChangeApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approved_target_hash",
                schema: "beacon",
                table: "data_sources",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encrypted_payload",
                schema: "beacon",
                table: "change_approval_requests",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_approval_requests_kind_resource_id",
                schema: "beacon",
                table: "change_approval_requests",
                columns: new[] { "kind", "resource_id" },
                unique: true,
                filter: "kind = 5 AND status = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_change_approval_requests_kind_resource_id",
                schema: "beacon",
                table: "change_approval_requests");

            migrationBuilder.DropColumn(
                name: "approved_target_hash",
                schema: "beacon",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "encrypted_payload",
                schema: "beacon",
                table: "change_approval_requests");
        }
    }
}
