using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAuditCallerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "caller_hash",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "caller_kind",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "caller_hash",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "caller_kind",
                schema: "beacon",
                table: "mcp_audit_logs");
        }
    }
}
