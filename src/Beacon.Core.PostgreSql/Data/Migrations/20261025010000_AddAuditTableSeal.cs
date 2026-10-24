using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTableSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "audit_guid",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hmac",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hmac_key_id",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "seal_version",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "audit_guid",
                schema: "beacon",
                table: "app_setting_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hmac",
                schema: "beacon",
                table: "app_setting_history",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hmac_key_id",
                schema: "beacon",
                table: "app_setting_history",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "seal_version",
                schema: "beacon",
                table: "app_setting_history",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_audit_guid",
                schema: "beacon",
                table: "manual_query_execution_logs",
                column: "audit_guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_setting_history_audit_guid",
                schema: "beacon",
                table: "app_setting_history",
                column: "audit_guid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_manual_query_execution_logs_audit_guid",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropIndex(
                name: "ix_app_setting_history_audit_guid",
                schema: "beacon",
                table: "app_setting_history");

            migrationBuilder.DropColumn(
                name: "audit_guid",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "hmac",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "hmac_key_id",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "seal_version",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "audit_guid",
                schema: "beacon",
                table: "app_setting_history");

            migrationBuilder.DropColumn(
                name: "hmac",
                schema: "beacon",
                table: "app_setting_history");

            migrationBuilder.DropColumn(
                name: "hmac_key_id",
                schema: "beacon",
                table: "app_setting_history");

            migrationBuilder.DropColumn(
                name: "seal_version",
                schema: "beacon",
                table: "app_setting_history");
        }
    }
}
