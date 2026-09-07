using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAskCorrectnessGrounding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_semantic_lint",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "enable_value_grounding",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "self_consistency_min_tables",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "value_grounding_max_probes",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<bool>(
                name: "sample_values_complete",
                table: "column_metadata",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_semantic_lint",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "enable_value_grounding",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "self_consistency_min_tables",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "value_grounding_max_probes",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "sample_values_complete",
                table: "column_metadata");
        }
    }
}
