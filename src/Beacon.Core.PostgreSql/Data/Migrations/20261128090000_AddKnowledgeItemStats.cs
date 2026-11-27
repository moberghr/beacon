using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeItemStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_knowledge_item_boost",
                schema: "beacon",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "enable_scheduled_eval",
                schema: "beacon",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "enable_knowledge_item_boost",
                schema: "beacon",
                table: "mcp_project_settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "enable_scheduled_eval",
                schema: "beacon",
                table: "mcp_project_settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "last_replay_passed",
                schema: "beacon",
                table: "mcp_learned_patterns",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trigger",
                schema: "beacon",
                table: "mcp_eval_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "source_pattern_id",
                schema: "beacon",
                table: "mcp_documentation_patches",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mcp_knowledge_item_stats",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    helpful_score = table.Column<double>(type: "double precision", nullable: false),
                    harmful_score = table.Column<double>(type: "double precision", nullable: false),
                    signal_count = table.Column<int>(type: "integer", nullable: false),
                    caller_count = table.Column<int>(type: "integer", nullable: false),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_knowledge_item_stats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_knowledge_item_stats_project_id_kind_item_id",
                schema: "beacon",
                table: "mcp_knowledge_item_stats",
                columns: new[] { "project_id", "kind", "item_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_knowledge_item_stats",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "enable_knowledge_item_boost",
                schema: "beacon",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "enable_scheduled_eval",
                schema: "beacon",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "enable_knowledge_item_boost",
                schema: "beacon",
                table: "mcp_project_settings");

            migrationBuilder.DropColumn(
                name: "enable_scheduled_eval",
                schema: "beacon",
                table: "mcp_project_settings");

            migrationBuilder.DropColumn(
                name: "last_replay_passed",
                schema: "beacon",
                table: "mcp_learned_patterns");

            migrationBuilder.DropColumn(
                name: "trigger",
                schema: "beacon",
                table: "mcp_eval_runs");

            migrationBuilder.DropColumn(
                name: "source_pattern_id",
                schema: "beacon",
                table: "mcp_documentation_patches");
        }
    }
}
