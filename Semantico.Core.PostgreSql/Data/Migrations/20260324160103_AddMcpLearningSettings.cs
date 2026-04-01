using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpLearningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_learning",
                schema: "semantico",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "learning_auto_approve_threshold",
                schema: "semantico",
                table: "mcp_settings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "learning_injection_budget_chars",
                schema: "semantico",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "learning_signal_retention_days",
                schema: "semantico",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_learning",
                schema: "semantico",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "learning_auto_approve_threshold",
                schema: "semantico",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "learning_injection_budget_chars",
                schema: "semantico",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "learning_signal_retention_days",
                schema: "semantico",
                table: "mcp_settings");
        }
    }
}
