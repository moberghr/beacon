using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Removes the tables of features that no code reads or writes any more (dashboards, MCP sessions, AI alert
            // configurations and their conversation history, AI usage metrics) and the audit log's key to MCP sessions, which was never
            // set. The live Mcp-Session-Id header column (McpSessionId) stays.
            migrationBuilder.DropForeignKey(
                name: "FK_McpAuditLogs_McpSessions_SessionId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_McpAuditLogs_SessionId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropColumn(
                name: "SessionId",
                schema: "beacon",
                table: "McpAuditLogs");

            migrationBuilder.DropTable(
                name: "AiConversationHistories",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiAlertConfigurations",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DashboardPermissions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DashboardWidgets",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Dashboards",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpSessions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiUsageMetrics",
                schema: "beacon");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiUsageMetrics",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    QueryId = table.Column<int>(type: "int", nullable: true),
                    DocumentationId = table.Column<int>(type: "int", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OperationType = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    TotalTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PromptCacheHit = table.Column<bool>(type: "bit", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiUsageMetrics_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AiUsageMetrics_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_DataSourceId",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_OperationType",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "OperationType");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_Provider",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_QueryId",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_Timestamp",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_UserId",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "UserId");

            migrationBuilder.CreateTable(
                name: "McpSessions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiKeyId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false),
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
                        principalSchema: "beacon",
                        principalTable: "ApiKeyCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_McpSessions_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "beacon",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Dashboards",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    RefreshIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    LayoutConfiguration = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DashboardPermissions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PermissionLevel = table.Column<int>(type: "int", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardPermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardPermissions_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "beacon",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DashboardWidgets",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WidgetType = table.Column<int>(type: "int", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PositionX = table.Column<int>(type: "int", nullable: false),
                    PositionY = table.Column<int>(type: "int", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RefreshIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardWidgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardWidgets_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "beacon",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiAlertConfigurations",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NaturalLanguageDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedSql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FinalSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GeneratedByModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GenerationReasoning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfidenceScore = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ValidationErrors = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserFeedback = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubscriptionId = table.Column<int>(type: "int", nullable: true),
                    ConversationTurns = table.Column<int>(type: "int", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAlertConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiAlertConfigurations_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiAlertConfigurations_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "beacon",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiConversationHistories",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AiAlertConfigurationId = table.Column<int>(type: "int", nullable: false),
                    TurnNumber = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    MessageContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConversationHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiConversationHistories_AiAlertConfigurations_AiAlertConfigurationId",
                        column: x => x.AiAlertConfigurationId,
                        principalSchema: "beacon",
                        principalTable: "AiAlertConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddColumn<int>(
                name: "SessionId",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_ApiKeyId",
                schema: "beacon",
                table: "McpSessions",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_LastActivityAt",
                schema: "beacon",
                table: "McpSessions",
                column: "LastActivityAt");

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_SessionId",
                schema: "beacon",
                table: "McpSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpSessions_UserId",
                schema: "beacon",
                table: "McpSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_ArchivedTime",
                schema: "beacon",
                table: "Dashboards",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_CreatedByUserId",
                schema: "beacon",
                table: "Dashboards",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_IsDefault",
                schema: "beacon",
                table: "Dashboards",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_IsShared",
                schema: "beacon",
                table: "Dashboards",
                column: "IsShared");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardPermissions_DashboardId",
                schema: "beacon",
                table: "DashboardPermissions",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardPermissions_DashboardId_UserId",
                schema: "beacon",
                table: "DashboardPermissions",
                columns: new[] { "DashboardId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardPermissions_UserId",
                schema: "beacon",
                table: "DashboardPermissions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId",
                schema: "beacon",
                table: "DashboardWidgets",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId_SortOrder",
                schema: "beacon",
                table: "DashboardWidgets",
                columns: new[] { "DashboardId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_WidgetType",
                schema: "beacon",
                table: "DashboardWidgets",
                column: "WidgetType");

            migrationBuilder.CreateIndex(
                name: "IX_AiAlertConfigurations_DataSourceId",
                schema: "beacon",
                table: "AiAlertConfigurations",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAlertConfigurations_Status",
                schema: "beacon",
                table: "AiAlertConfigurations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiAlertConfigurations_SubscriptionId",
                schema: "beacon",
                table: "AiAlertConfigurations",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationHistories_AiAlertConfigurationId",
                schema: "beacon",
                table: "AiConversationHistories",
                column: "AiAlertConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationHistories_Timestamp",
                schema: "beacon",
                table: "AiConversationHistories",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationHistories_TurnNumber",
                schema: "beacon",
                table: "AiConversationHistories",
                column: "TurnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_SessionId",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_McpAuditLogs_McpSessions_SessionId",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "SessionId",
                principalSchema: "beacon",
                principalTable: "McpSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
