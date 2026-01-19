using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class Refactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documentation_versions_created_at",
                schema: "semantico",
                table: "documentation_versions");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "semantico",
                table: "documentation_versions");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "semantico",
                table: "documentation_sections");

            migrationBuilder.DropColumn(
                name: "modified_at",
                schema: "semantico",
                table: "documentation_sections");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "semantico",
                table: "data_source_documentations");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "semantico",
                table: "data_source_documentations");

            migrationBuilder.DropColumn(
                name: "modified_at",
                schema: "semantico",
                table: "data_source_documentations");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "semantico",
                table: "ai_prompt_templates");

            migrationBuilder.DropColumn(
                name: "modified_at",
                schema: "semantico",
                table: "ai_prompt_templates");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "semantico",
                table: "ai_alert_configurations");

            migrationBuilder.DropColumn(
                name: "modified_at",
                schema: "semantico",
                table: "ai_alert_configurations");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_versions_created_time",
                schema: "semantico",
                table: "documentation_versions",
                column: "created_time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documentation_versions_created_time",
                schema: "semantico",
                table: "documentation_versions");

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "documentation_versions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "documentation_sections",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "documentation_sections",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "data_source_documentations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "semantico",
                table: "data_source_documentations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "data_source_documentations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "ai_prompt_templates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "ai_prompt_templates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "ai_alert_configurations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "ai_alert_configurations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "ix_documentation_versions_created_at",
                schema: "semantico",
                table: "documentation_versions",
                column: "created_at");
        }
    }
}
