using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpSelfLearningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_self_consistency",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "self_consistency_candidate_count",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "enable_eval_judge",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "enable_semantic_retrieval",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "exemplar_top_k",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_self_consistency",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "self_consistency_candidate_count",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "enable_eval_judge",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "enable_semantic_retrieval",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "exemplar_top_k",
                table: "mcp_settings");
        }
    }
}
