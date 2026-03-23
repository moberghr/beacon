using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectCentricMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentationAgentRuns",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DocumentationSections",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DocumentationVersions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ProjectReports",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "SchemaChanges",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "SchemaSnapshots",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataSourceDocumentations",
                schema: "semantico");

            migrationBuilder.RenameColumn(
                name: "ListDataSourcesDescription",
                schema: "semantico",
                table: "McpSettings",
                newName: "SearchDescription");

            migrationBuilder.RenameColumn(
                name: "AllowedDataSourceIds",
                schema: "semantico",
                table: "ApiKeyCredentials",
                newName: "AllowedProjectIds");

            migrationBuilder.AddColumn<string>(
                name: "GetContextDescription",
                schema: "semantico",
                table: "McpSettings",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                schema: "semantico",
                table: "McpAuditLogs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectDocumentations",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    GeneratedByModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedByUserId = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DataSourcesAnalyzed = table.Column<int>(type: "int", nullable: false),
                    TablesAnalyzed = table.Column<int>(type: "int", nullable: false),
                    CodeReferencesAnalyzed = table.Column<int>(type: "int", nullable: false),
                    GenerationDuration = table.Column<TimeSpan>(type: "time", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "semantico",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentationSections",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectDocumentationId = table.Column<int>(type: "int", nullable: false),
                    SectionType = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentationSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentationSections_ProjectDocumentations_ProjectDocumentationId",
                        column: x => x.ProjectDocumentationId,
                        principalSchema: "semantico",
                        principalTable: "ProjectDocumentations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentations_GeneratedAt",
                schema: "semantico",
                table: "ProjectDocumentations",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentations_ProjectId",
                schema: "semantico",
                table: "ProjectDocumentations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentationSections_ProjectDocumentationId",
                schema: "semantico",
                table: "ProjectDocumentationSections",
                column: "ProjectDocumentationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentationSections_SectionType",
                schema: "semantico",
                table: "ProjectDocumentationSections",
                column: "SectionType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectDocumentationSections",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ProjectDocumentations",
                schema: "semantico");

            migrationBuilder.DropColumn(
                name: "GetContextDescription",
                schema: "semantico",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "semantico",
                table: "McpAuditLogs");

            migrationBuilder.RenameColumn(
                name: "SearchDescription",
                schema: "semantico",
                table: "McpSettings",
                newName: "ListDataSourcesDescription");

            migrationBuilder.RenameColumn(
                name: "AllowedProjectIds",
                schema: "semantico",
                table: "ApiKeyCredentials",
                newName: "AllowedDataSourceIds");

            migrationBuilder.CreateTable(
                name: "DataSourceDocumentations",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedByModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedByUserId = table.Column<int>(type: "int", nullable: false),
                    LastModifiedByUserId = table.Column<int>(type: "int", nullable: true),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TablesAnalyzed = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSourceDocumentations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataSourceDocumentations_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectReports",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReportFormat = table.Column<int>(type: "int", nullable: false),
                    ReportType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectReports_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "semantico",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaChanges",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    ChangeType = table.Column<int>(type: "int", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NewValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaChanges_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaSnapshots",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaSnapshots_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentationAgentRuns",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    DocumentationId = table.Column<int>(type: "int", nullable: true),
                    CheckpointStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedTablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrentBatchIndex = table.Column<int>(type: "int", nullable: false),
                    CurrentPhase = table.Column<int>(type: "int", nullable: false),
                    DiscoveredTablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DomainGroupsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    FailedTablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastCheckpointAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ProgressMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedByUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TablesCompleted = table.Column<int>(type: "int", nullable: false),
                    TablesFailed = table.Column<int>(type: "int", nullable: false),
                    TotalTablesDiscovered = table.Column<int>(type: "int", nullable: false),
                    TotalTokensUsed = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentationAgentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentationAgentRuns_DataSourceDocumentations_DocumentationId",
                        column: x => x.DocumentationId,
                        principalSchema: "semantico",
                        principalTable: "DataSourceDocumentations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DocumentationAgentRuns_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentationSections",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentationId = table.Column<int>(type: "int", nullable: false),
                    AiGeneratedContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentFormat = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsUserEdited = table.Column<bool>(type: "bit", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SectionType = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserEditedContent = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentationSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentationSections_DataSourceDocumentations_DocumentationId",
                        column: x => x.DocumentationId,
                        principalSchema: "semantico",
                        principalTable: "DataSourceDocumentations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentationVersions",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentationId = table.Column<int>(type: "int", nullable: false),
                    ChangeDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SectionsCount = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: true),
                    VersionNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentationVersions_DataSourceDocumentations_DocumentationId",
                        column: x => x.DocumentationId,
                        principalSchema: "semantico",
                        principalTable: "DataSourceDocumentations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceDocumentations_DataSourceId",
                schema: "semantico",
                table: "DataSourceDocumentations",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceDocumentations_GeneratedAt",
                schema: "semantico",
                table: "DataSourceDocumentations",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceDocumentations_Status",
                schema: "semantico",
                table: "DataSourceDocumentations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationAgentRuns_CurrentPhase",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                column: "CurrentPhase");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationAgentRuns_DataSourceId_Status",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                columns: new[] { "DataSourceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationAgentRuns_DocumentationId",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                column: "DocumentationId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationAgentRuns_StartedAt",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationAgentRuns_Status",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationSections_DocumentationId",
                schema: "semantico",
                table: "DocumentationSections",
                column: "DocumentationId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationSections_SectionType",
                schema: "semantico",
                table: "DocumentationSections",
                column: "SectionType");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationSections_TableName",
                schema: "semantico",
                table: "DocumentationSections",
                column: "TableName");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationVersions_CreatedTime",
                schema: "semantico",
                table: "DocumentationVersions",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationVersions_DocumentationId",
                schema: "semantico",
                table: "DocumentationVersions",
                column: "DocumentationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectReports_GeneratedAt",
                schema: "semantico",
                table: "ProjectReports",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectReports_ProjectId",
                schema: "semantico",
                table: "ProjectReports",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChanges_ChangeType",
                schema: "semantico",
                table: "SchemaChanges",
                column: "ChangeType");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChanges_DataSourceId",
                schema: "semantico",
                table: "SchemaChanges",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChanges_DetectedAt",
                schema: "semantico",
                table: "SchemaChanges",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaSnapshots_CapturedAt",
                schema: "semantico",
                table: "SchemaSnapshots",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaSnapshots_DataSourceId",
                schema: "semantico",
                table: "SchemaSnapshots",
                column: "DataSourceId");
        }
    }
}
