using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class MCPCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "semantico",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TransformationApplied",
                schema: "semantico",
                table: "MigrationExecutions");

            migrationBuilder.DropColumn(
                name: "TablesExplored",
                schema: "semantico",
                table: "McpSessions");

            migrationBuilder.DropColumn(
                name: "AcknowledgedTime",
                schema: "semantico",
                table: "AnomalyEvents");

            migrationBuilder.DropColumn(
                name: "AlertConfigId",
                schema: "semantico",
                table: "AiUsageMetrics");

            migrationBuilder.DropColumn(
                name: "ResponseTimeMs",
                schema: "semantico",
                table: "AiUsageMetrics");

            migrationBuilder.DropColumn(
                name: "VariableDefinitions",
                schema: "semantico",
                table: "AiPromptTemplates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "semantico",
                table: "Projects",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransformationApplied",
                schema: "semantico",
                table: "MigrationExecutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TablesExplored",
                schema: "semantico",
                table: "McpSessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedTime",
                schema: "semantico",
                table: "AnomalyEvents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlertConfigId",
                schema: "semantico",
                table: "AiUsageMetrics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponseTimeMs",
                schema: "semantico",
                table: "AiUsageMetrics",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VariableDefinitions",
                schema: "semantico",
                table: "AiPromptTemplates",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }
    }
}
