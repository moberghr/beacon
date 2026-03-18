using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectCentricMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "list_data_sources_description",
                schema: "semantico",
                table: "mcp_settings",
                newName: "search_description");

            migrationBuilder.RenameColumn(
                name: "allowed_data_source_ids",
                schema: "semantico",
                table: "api_key_credentials",
                newName: "allowed_project_ids");

            migrationBuilder.AddColumn<string>(
                name: "get_context_description",
                schema: "semantico",
                table: "mcp_settings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "project_id",
                schema: "semantico",
                table: "mcp_audit_logs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "get_context_description",
                schema: "semantico",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "project_id",
                schema: "semantico",
                table: "mcp_audit_logs");

            migrationBuilder.RenameColumn(
                name: "search_description",
                schema: "semantico",
                table: "mcp_settings",
                newName: "list_data_sources_description");

            migrationBuilder.RenameColumn(
                name: "allowed_project_ids",
                schema: "semantico",
                table: "api_key_credentials",
                newName: "allowed_data_source_ids");
        }
    }
}
