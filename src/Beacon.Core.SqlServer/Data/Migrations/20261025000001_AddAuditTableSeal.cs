using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTableSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AuditGuid",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hmac",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HmacKeyId",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SealVersion",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AuditGuid",
                schema: "beacon",
                table: "AppSettingHistory",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hmac",
                schema: "beacon",
                table: "AppSettingHistory",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HmacKeyId",
                schema: "beacon",
                table: "AppSettingHistory",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SealVersion",
                schema: "beacon",
                table: "AppSettingHistory",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_AuditGuid",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                column: "AuditGuid",
                unique: true,
                filter: "[AuditGuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettingHistory_AuditGuid",
                schema: "beacon",
                table: "AppSettingHistory",
                column: "AuditGuid",
                unique: true,
                filter: "[AuditGuid] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualQueryExecutionLogs_AuditGuid",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropIndex(
                name: "IX_AppSettingHistory_AuditGuid",
                schema: "beacon",
                table: "AppSettingHistory");

            migrationBuilder.DropColumn(
                name: "AuditGuid",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "Hmac",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "HmacKeyId",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "SealVersion",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "AuditGuid",
                schema: "beacon",
                table: "AppSettingHistory");

            migrationBuilder.DropColumn(
                name: "Hmac",
                schema: "beacon",
                table: "AppSettingHistory");

            migrationBuilder.DropColumn(
                name: "HmacKeyId",
                schema: "beacon",
                table: "AppSettingHistory");

            migrationBuilder.DropColumn(
                name: "SealVersion",
                schema: "beacon",
                table: "AppSettingHistory");
        }
    }
}
