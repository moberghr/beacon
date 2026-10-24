using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAuditSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AuditGuid",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hmac",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HmacKeyId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SealVersion",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_AuditGuid",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "AuditGuid",
                unique: true,
                filter: "[AuditGuid] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McpAuditLogs_AuditGuid",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "AuditGuid",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "Hmac",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "HmacKeyId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "SealVersion",
                schema: "beacon",
                table: "McpAuditLogs");
        }
    }
}
