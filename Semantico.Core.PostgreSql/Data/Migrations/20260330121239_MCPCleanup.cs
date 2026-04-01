using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class MCPCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "semantico",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "transformation_applied",
                schema: "semantico",
                table: "migration_executions");

            migrationBuilder.DropColumn(
                name: "tables_explored",
                schema: "semantico",
                table: "mcp_sessions");

            migrationBuilder.DropColumn(
                name: "acknowledged_time",
                schema: "semantico",
                table: "anomaly_events");

            migrationBuilder.DropColumn(
                name: "alert_config_id",
                schema: "semantico",
                table: "ai_usage_metrics");

            migrationBuilder.DropColumn(
                name: "response_time_ms",
                schema: "semantico",
                table: "ai_usage_metrics");

            migrationBuilder.DropColumn(
                name: "variable_definitions",
                schema: "semantico",
                table: "ai_prompt_templates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                schema: "semantico",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transformation_applied",
                schema: "semantico",
                table: "migration_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tables_explored",
                schema: "semantico",
                table: "mcp_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "acknowledged_time",
                schema: "semantico",
                table: "anomaly_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "alert_config_id",
                schema: "semantico",
                table: "ai_usage_metrics",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "response_time_ms",
                schema: "semantico",
                table: "ai_usage_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "variable_definitions",
                schema: "semantico",
                table: "ai_prompt_templates",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
