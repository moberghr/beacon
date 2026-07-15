using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpEval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpEvalCases",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    Question = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    GoldSql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GoldResultFingerprint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceSignalId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpEvalCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpEvalRuns",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    TriggeredByUserId = table.Column<int>(type: "int", nullable: true),
                    TotalCases = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    PassedCases = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ExecutionAccuracy = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    JudgeEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpEvalRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpEvalResults",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvalRunId = table.Column<int>(type: "int", nullable: false),
                    EvalCaseId = table.Column<int>(type: "int", nullable: false),
                    GeneratedSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Passed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    FailureTag = table.Column<int>(type: "int", nullable: false),
                    ExecutionError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    JudgeUsed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    JudgeVerdict = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ResultRowCount = table.Column<int>(type: "int", nullable: true),
                    ExecutionTimeMs = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpEvalResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpEvalCases_DataSourceId_IsActive",
                schema: "beacon",
                table: "McpEvalCases",
                columns: new[] { "DataSourceId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_McpEvalResults_EvalCaseId",
                schema: "beacon",
                table: "McpEvalResults",
                column: "EvalCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_McpEvalResults_EvalRunId",
                schema: "beacon",
                table: "McpEvalResults",
                column: "EvalRunId");

            migrationBuilder.CreateIndex(
                name: "IX_McpEvalRuns_CreatedTime",
                schema: "beacon",
                table: "McpEvalRuns",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_McpEvalRuns_ProjectId",
                schema: "beacon",
                table: "McpEvalRuns",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpEvalCases",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpEvalResults",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpEvalRuns",
                schema: "beacon");
        }
    }
}
