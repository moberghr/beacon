using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpProjectSettingsAndLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowExplicitFeedbackContent",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentQueriesPerKey",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxExplainCost",
                schema: "beacon",
                table: "McpSettings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxResultBytes",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 262144);

            migrationBuilder.AddColumn<bool>(
                name: "RetainQueryContent",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "StatementTimeoutSeconds",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "CallerHash",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReadOnly",
                schema: "beacon",
                table: "DataSources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseReadOnlyIntent",
                schema: "beacon",
                table: "DataSources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "McpProjectSettings",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    MaxRowLimit = table.Column<int>(type: "int", nullable: true),
                    EnforceReadOnly = table.Column<bool>(type: "bit", nullable: true),
                    EnablePiiDetection = table.Column<bool>(type: "bit", nullable: true),
                    CustomPiiPatterns = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EnableSampleValueCollection = table.Column<bool>(type: "bit", nullable: true),
                    EnableLearning = table.Column<bool>(type: "bit", nullable: true),
                    LearningAutoApproveThreshold = table.Column<double>(type: "float", nullable: true),
                    LearningInjectionBudgetChars = table.Column<int>(type: "int", nullable: true),
                    LearningSignalRetentionDays = table.Column<int>(type: "int", nullable: true),
                    EnableSelfConsistency = table.Column<bool>(type: "bit", nullable: true),
                    SelfConsistencyCandidateCount = table.Column<int>(type: "int", nullable: true),
                    EnableEvalJudge = table.Column<bool>(type: "bit", nullable: true),
                    EnableSemanticRetrieval = table.Column<bool>(type: "bit", nullable: true),
                    ExemplarTopK = table.Column<int>(type: "int", nullable: true),
                    EnableReplayVerification = table.Column<bool>(type: "bit", nullable: true),
                    LearningReplayMinFlips = table.Column<int>(type: "int", nullable: true),
                    EnableContextualRetrieval = table.Column<bool>(type: "bit", nullable: true),
                    DocChunkWindowSentences = table.Column<int>(type: "int", nullable: true),
                    DocChunkOverlapSentences = table.Column<int>(type: "int", nullable: true),
                    GlossaryTopK = table.Column<int>(type: "int", nullable: true),
                    DocChunkTopK = table.Column<int>(type: "int", nullable: true),
                    EnableGoldenExemplars = table.Column<bool>(type: "bit", nullable: true),
                    GoldenExemplarTopK = table.Column<int>(type: "int", nullable: true),
                    GoldenExemplarBudgetChars = table.Column<int>(type: "int", nullable: true),
                    EnableValueGrounding = table.Column<bool>(type: "bit", nullable: true),
                    ValueGroundingMaxProbes = table.Column<int>(type: "int", nullable: true),
                    EnableSemanticLint = table.Column<bool>(type: "bit", nullable: true),
                    SelfConsistencyMinTables = table.Column<int>(type: "int", nullable: true),
                    RetainQueryContent = table.Column<bool>(type: "bit", nullable: true),
                    StatementTimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    MaxResultBytes = table.Column<int>(type: "int", nullable: true),
                    MaxExplainCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxConcurrentQueriesPerKey = table.Column<int>(type: "int", nullable: true),
                    AllowExplicitFeedbackContent = table.Column<bool>(type: "bit", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpProjectSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpProjectSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpProjectSettings_ProjectId",
                schema: "beacon",
                table: "McpProjectSettings",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpProjectSettings",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "AllowExplicitFeedbackContent",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentQueriesPerKey",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "MaxExplainCost",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "MaxResultBytes",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "RetainQueryContent",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "StatementTimeoutSeconds",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "CallerHash",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "IsReadOnly",
                schema: "beacon",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "UseReadOnlyIntent",
                schema: "beacon",
                table: "DataSources");
        }
    }
}
