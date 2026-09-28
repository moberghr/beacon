using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAuditCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApiKeyId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "McpSessionId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpanId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamRequestId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "McpSessionId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "SpanId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "UpstreamRequestId",
                schema: "beacon",
                table: "McpAuditLogs");
        }
    }
}
