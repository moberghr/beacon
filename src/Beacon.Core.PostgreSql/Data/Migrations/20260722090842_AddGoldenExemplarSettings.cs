using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldenExemplarSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_golden_exemplars",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "golden_exemplar_budget_chars",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "golden_exemplar_top_k",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_golden_exemplars",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "golden_exemplar_budget_chars",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "golden_exemplar_top_k",
                table: "mcp_settings");
        }
    }
}
