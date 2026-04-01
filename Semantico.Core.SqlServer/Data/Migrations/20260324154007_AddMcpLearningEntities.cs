using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpLearningEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpDocumentationPatches",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    TargetType = table.Column<int>(type: "int", nullable: false),
                    TargetIdentifier = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CurrentContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProposedContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reasoning = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SupportingSignalCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AppliedByUserId = table.Column<int>(type: "int", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpDocumentationPatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpLearnedPatterns",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PatternType = table.Column<int>(type: "int", nullable: false),
                    PatternContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExampleQuestion = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExampleSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SignalCount = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReviewedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRefreshedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpLearnedPatterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpQuerySignals",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Tool = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Question = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IntentClassification = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RoutingDecision = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GeneratedSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TablesUsed = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ColumnsUsed = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SchemaValidationFailed = table.Column<bool>(type: "bit", nullable: false),
                    SchemaValidationError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExecutionFailed = table.Column<bool>(type: "bit", nullable: false),
                    ExecutionError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetryAttempted = table.Column<bool>(type: "bit", nullable: false),
                    RetrySucceeded = table.Column<bool>(type: "bit", nullable: false),
                    CorrectedSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultRowCount = table.Column<int>(type: "int", nullable: true),
                    ExecutionTimeMs = table.Column<int>(type: "int", nullable: false),
                    IsSuccessful = table.Column<bool>(type: "bit", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpQuerySignals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpDocumentationPatches_DataSourceId",
                schema: "semantico",
                table: "McpDocumentationPatches",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_McpDocumentationPatches_ProjectId_Status",
                schema: "semantico",
                table: "McpDocumentationPatches",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_McpLearnedPatterns_DataSourceId_Status_TableName",
                schema: "semantico",
                table: "McpLearnedPatterns",
                columns: new[] { "DataSourceId", "Status", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_McpLearnedPatterns_ProjectId",
                schema: "semantico",
                table: "McpLearnedPatterns",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_CreatedTime",
                schema: "semantico",
                table: "McpQuerySignals",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_DataSourceId",
                schema: "semantico",
                table: "McpQuerySignals",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_IsSuccessful",
                schema: "semantico",
                table: "McpQuerySignals",
                column: "IsSuccessful");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_ProjectId",
                schema: "semantico",
                table: "McpQuerySignals",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpDocumentationPatches",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "McpLearnedPatterns",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "McpQuerySignals",
                schema: "semantico");
        }
    }
}
