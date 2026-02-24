using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataContracts",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CronExpression = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AlertOnFailure = table.Column<bool>(type: "bit", nullable: false),
                    FailureThresholdScore = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataContracts_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DataQualityScores",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Score = table.Column<double>(type: "float", nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TrendDirection = table.Column<int>(type: "int", nullable: false),
                    PreviousScore = table.Column<double>(type: "float", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataQualityScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataQualityScores_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DataContractRules",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataContractId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RuleType = table.Column<int>(type: "int", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Configuration = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Weight = table.Column<double>(type: "float", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataContractRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataContractRules_DataContracts_DataContractId",
                        column: x => x.DataContractId,
                        principalSchema: "semantico",
                        principalTable: "DataContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataQualityEvaluations",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataContractId = table.Column<int>(type: "int", nullable: false),
                    OverallScore = table.Column<double>(type: "float", nullable: false),
                    PassedRules = table.Column<int>(type: "int", nullable: false),
                    FailedRules = table.Column<int>(type: "int", nullable: false),
                    TotalRules = table.Column<int>(type: "int", nullable: false),
                    ExecutionTimeMs = table.Column<double>(type: "float", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataQualityEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataQualityEvaluations_DataContracts_DataContractId",
                        column: x => x.DataContractId,
                        principalSchema: "semantico",
                        principalTable: "DataContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DataQualityRuleResults",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataQualityEvaluationId = table.Column<int>(type: "int", nullable: false),
                    DataContractRuleId = table.Column<int>(type: "int", nullable: false),
                    Passed = table.Column<bool>(type: "bit", nullable: false),
                    Score = table.Column<double>(type: "float", nullable: false),
                    ActualValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpectedValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExecutionTimeMs = table.Column<double>(type: "float", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataQualityRuleResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataQualityRuleResults_DataContractRules_DataContractRuleId",
                        column: x => x.DataContractRuleId,
                        principalSchema: "semantico",
                        principalTable: "DataContractRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataQualityRuleResults_DataQualityEvaluations_DataQualityEvaluationId",
                        column: x => x.DataQualityEvaluationId,
                        principalSchema: "semantico",
                        principalTable: "DataQualityEvaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataContractRules_DataContractId",
                schema: "semantico",
                table: "DataContractRules",
                column: "DataContractId");

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_ArchivedTime",
                schema: "semantico",
                table: "DataContracts",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_DataSourceId",
                schema: "semantico",
                table: "DataContracts",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_DataSourceId_SchemaName_TableName",
                schema: "semantico",
                table: "DataContracts",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_IsEnabled",
                schema: "semantico",
                table: "DataContracts",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityEvaluations_CreatedTime",
                schema: "semantico",
                table: "DataQualityEvaluations",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityEvaluations_DataContractId",
                schema: "semantico",
                table: "DataQualityEvaluations",
                column: "DataContractId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityRuleResults_DataContractRuleId",
                schema: "semantico",
                table: "DataQualityRuleResults",
                column: "DataContractRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityRuleResults_DataQualityEvaluationId",
                schema: "semantico",
                table: "DataQualityRuleResults",
                column: "DataQualityEvaluationId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityScores_DataSourceId_SchemaName_TableName",
                schema: "semantico",
                table: "DataQualityScores",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityScores_EvaluatedAt",
                schema: "semantico",
                table: "DataQualityScores",
                column: "EvaluatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataQualityRuleResults",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataQualityScores",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataContractRules",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataQualityEvaluations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataContracts",
                schema: "semantico");
        }
    }
}
