using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAuditCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "api_key_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mcp_session_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "span_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_request_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "api_key_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "mcp_session_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "span_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "trace_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "upstream_request_id",
                schema: "beacon",
                table: "mcp_audit_logs");
        }
    }
}
