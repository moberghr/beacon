using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAuditCallerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallerHash",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CallerKind",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CallerHash",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "CallerKind",
                schema: "beacon",
                table: "McpAuditLogs");
        }
    }
}
