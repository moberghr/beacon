using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeyCredentials",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowedDataSourceIds = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeyCredentials_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "semantico",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "McpSettings",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AskSystemPrompt = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    GlobalInstruction = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ListDataSourcesDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    QueryDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    GetDocumentationDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AskDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MaxRowLimit = table.Column<int>(type: "int", nullable: false),
                    EnforceReadOnly = table.Column<bool>(type: "bit", nullable: false),
                    EnablePiiDetection = table.Column<bool>(type: "bit", nullable: false),
                    CustomPiiPatterns = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaChanges",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangeType = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                    SchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                name: "McpSessions",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiKeyId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TablesExplored = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    QueriesExecuted = table.Column<int>(type: "int", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpSessions_ApiKeyCredentials_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalSchema: "semantico",
                        principalTable: "ApiKeyCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_McpSessions_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "semantico",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GitHubRepositories",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastScanAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScanStatus = table.Column<int>(type: "int", nullable: false),
                    ScanCronExpression = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastScanError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TotalFilesScanned = table.Column<int>(type: "int", nullable: false),
                    TotalReferencesFound = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubRepositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubRepositories_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "semantico",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDataSources",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDataSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDataSources_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectDataSources_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "semantico",
                        principalTable: "Projects",
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
                    ReportType = table.Column<int>(type: "int", nullable: false),
                    ReportFormat = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                name: "McpAuditLogs",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Tool = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Parameters = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    ExecutionTimeMs = table.Column<int>(type: "int", nullable: false),
                    ResultRowCount = table.Column<int>(type: "int", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpAuditLogs_McpSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "semantico",
                        principalTable: "McpSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_McpAuditLogs_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "semantico",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CodeReferences",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GitHubRepositoryId = table.Column<int>(type: "int", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: true),
                    ReferenceType = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CodeSnippet = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ClassName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MethodName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodeReferences_GitHubRepositories_GitHubRepositoryId",
                        column: x => x.GitHubRepositoryId,
                        principalSchema: "semantico",
                        principalTable: "GitHubRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_IsRevoked",
                schema: "semantico",
                table: "ApiKeyCredentials",
                column: "IsRevoked");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_KeyHash",
                schema: "semantico",
                table: "ApiKeyCredentials",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_KeyPrefix",
                schema: "semantico",
                table: "ApiKeyCredentials",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_UserId",
                schema: "semantico",
                table: "ApiKeyCredentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeReferences_GitHubRepositoryId",
                schema: "semantico",
                table: "CodeReferences",
                column: "GitHubRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeReferences_ReferenceType",
                schema: "semantico",
                table: "CodeReferences",
                column: "ReferenceType");

            migrationBuilder.CreateIndex(
                name: "IX_CodeReferences_SchemaName_TableName",
                schema: "semantico",
                table: "CodeReferences",
                columns: new[] { "SchemaName", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubRepositories_ProjectId",
                schema: "semantico",
                table: "GitHubRepositories",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubRepositories_ScanStatus",
                schema: "semantico",
                table: "GitHubRepositories",
                column: "ScanStatus");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_CreatedTime",
                schema: "semantico",
                table: "McpAuditLogs",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_SessionId",
                schema: "semantico",
                table: "McpAuditLogs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_Tool",
                schema: "semantico",
                table: "McpAuditLogs",
                column: "Tool");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_UserId",
                schema: "semantico",
                table: "McpAuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_ApiKeyId",
                schema: "semantico",
                table: "McpSessions",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_LastActivityAt",
                schema: "semantico",
                table: "McpSessions",
                column: "LastActivityAt");

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_SessionId",
                schema: "semantico",
                table: "McpSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_UserId",
                schema: "semantico",
                table: "McpSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDataSources_DataSourceId",
                schema: "semantico",
                table: "ProjectDataSources",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDataSources_ProjectId_DataSourceId",
                schema: "semantico",
                table: "ProjectDataSources",
                columns: new[] { "ProjectId", "DataSourceId" },
                unique: true);

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
                name: "IX_Projects_Name",
                schema: "semantico",
                table: "Projects",
                column: "Name");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodeReferences",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "McpAuditLogs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "McpSettings",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ProjectDataSources",
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
                name: "GitHubRepositories",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "McpSessions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ApiKeyCredentials",
                schema: "semantico");
        }
    }
}
