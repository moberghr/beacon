using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpLearningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableLearning",
                schema: "semantico",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "LearningAutoApproveThreshold",
                schema: "semantico",
                table: "McpSettings",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "LearningInjectionBudgetChars",
                schema: "semantico",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LearningSignalRetentionDays",
                schema: "semantico",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableLearning",
                schema: "semantico",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "LearningAutoApproveThreshold",
                schema: "semantico",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "LearningInjectionBudgetChars",
                schema: "semantico",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "LearningSignalRetentionDays",
                schema: "semantico",
                table: "McpSettings");
        }
    }
}
