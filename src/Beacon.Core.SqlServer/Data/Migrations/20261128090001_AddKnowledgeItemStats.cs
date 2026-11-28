using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeItemStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableKnowledgeItemBoost",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableScheduledEval",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableKnowledgeItemBoost",
                schema: "beacon",
                table: "McpProjectSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableScheduledEval",
                schema: "beacon",
                table: "McpProjectSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastReplayPassed",
                schema: "beacon",
                table: "McpLearnedPatterns",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Trigger",
                schema: "beacon",
                table: "McpEvalRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SourcePatternId",
                schema: "beacon",
                table: "McpDocumentationPatches",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "McpKnowledgeItemStats",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    HelpfulScore = table.Column<double>(type: "float", nullable: false),
                    HarmfulScore = table.Column<double>(type: "float", nullable: false),
                    SignalCount = table.Column<int>(type: "int", nullable: false),
                    CallerCount = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpKnowledgeItemStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpKnowledgeItemStats_ProjectId_Kind_ItemId",
                schema: "beacon",
                table: "McpKnowledgeItemStats",
                columns: new[] { "ProjectId", "Kind", "ItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpKnowledgeItemStats",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "EnableKnowledgeItemBoost",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "EnableScheduledEval",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "EnableKnowledgeItemBoost",
                schema: "beacon",
                table: "McpProjectSettings");

            migrationBuilder.DropColumn(
                name: "EnableScheduledEval",
                schema: "beacon",
                table: "McpProjectSettings");

            migrationBuilder.DropColumn(
                name: "LastReplayPassed",
                schema: "beacon",
                table: "McpLearnedPatterns");

            migrationBuilder.DropColumn(
                name: "Trigger",
                schema: "beacon",
                table: "McpEvalRuns");

            migrationBuilder.DropColumn(
                name: "SourcePatternId",
                schema: "beacon",
                table: "McpDocumentationPatches");
        }
    }
}
