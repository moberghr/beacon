using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAuditSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "audit_guid",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hmac",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hmac_key_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "seal_version",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_audit_guid",
                schema: "beacon",
                table: "mcp_audit_logs",
                column: "audit_guid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_mcp_audit_logs_audit_guid",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "audit_guid",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "hmac",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "hmac_key_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "seal_version",
                schema: "beacon",
                table: "mcp_audit_logs");
        }
    }
}
