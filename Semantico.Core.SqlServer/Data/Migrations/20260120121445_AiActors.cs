using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiActors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueryTasks_SubscriptionId",
                schema: "semantico",
                table: "QueryTasks");

            migrationBuilder.AddColumn<int>(
                name: "AiActorId",
                schema: "semantico",
                table: "Subscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimumRowCount",
                schema: "semantico",
                table: "Subscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NotificationTrigger",
                schema: "semantico",
                table: "Subscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiActorId",
                schema: "semantico",
                table: "Queries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                schema: "semantico",
                table: "Queries",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                schema: "semantico",
                table: "Queries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedByUserId",
                schema: "semantico",
                table: "Queries",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiActors",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AdditionalContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    MaxQueries = table.Column<int>(type: "int", nullable: false),
                    MaxSubscriptionsPerQuery = table.Column<int>(type: "int", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TotalTokensUsed = table.Column<int>(type: "int", nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LastThinkTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ThinkCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiActors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiActors_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiAlertConfigurations",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiAlertConfigurations_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiPromptTemplates",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OperationType = table.Column<int>(type: "int", nullable: false),
                    PromptTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Temperature = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MaxTokens = table.Column<int>(type: "int", nullable: false),
                    VariableDefinitions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageMetrics",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    QueryId = table.Column<int>(type: "int", nullable: true),
                    DocumentationId = table.Column<int>(type: "int", nullable: true),
                    AlertConfigId = table.Column<int>(type: "int", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OperationType = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    TotalTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PromptCacheHit = table.Column<bool>(type: "bit", nullable: false),
                    ResponseTimeMs = table.Column<int>(type: "int", nullable: false),
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
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AiUsageMetrics_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "semantico",
                        principalTable: "Queries",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AnomalyBaselines",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    ExecutionTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MetricValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnomalyBaselines_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnomalyConfigs",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DetectionMethod = table.Column<int>(type: "int", nullable: false),
                    Sensitivity = table.Column<int>(type: "int", nullable: false),
                    LookbackDays = table.Column<int>(type: "int", nullable: false),
                    AlertOnIncrease = table.Column<bool>(type: "bit", nullable: false),
                    AlertOnDecrease = table.Column<bool>(type: "bit", nullable: false),
                    MinimumDataPoints = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnomalyConfigs_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnomalyEvents",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    NotificationId = table.Column<int>(type: "int", nullable: true),
                    DetectedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrentValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BaselineMean = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BaselineStdDev = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ZScore = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Acknowledged = table.Column<bool>(type: "bit", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcknowledgedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnomalyEvents_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalSchema: "semantico",
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AnomalyEvents_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataSourceDocumentations",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedByModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedByUserId = table.Column<int>(type: "int", nullable: false),
                    LastModifiedByUserId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TablesAnalyzed = table.Column<int>(type: "int", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
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
                name: "AiActorPlans",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AiActorId = table.Column<int>(type: "int", nullable: false),
                    AiActorExecutionId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    UserInstruction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Analysis = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FindingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProposedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReviewerComment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ParentPlanId = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiActorPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiActorPlans_AiActorPlans_ParentPlanId",
                        column: x => x.ParentPlanId,
                        principalSchema: "semantico",
                        principalTable: "AiActorPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiActorPlans_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "semantico",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiConversationHistories",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "AiAlertConfigurations",
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
                    StartedByUserId = table.Column<int>(type: "int", nullable: false),
                    CurrentPhase = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    ProgressMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TotalTablesDiscovered = table.Column<int>(type: "int", nullable: false),
                    DiscoveredTablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DomainGroupsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TablesCompleted = table.Column<int>(type: "int", nullable: false),
                    TablesFailed = table.Column<int>(type: "int", nullable: false),
                    CompletedTablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailedTablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CurrentBatchIndex = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    TotalTokensUsed = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LastCheckpointAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CheckpointStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentationSections",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentationId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SectionType = table.Column<int>(type: "int", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    AiGeneratedContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserEditedContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsUserEdited = table.Column<bool>(type: "bit", nullable: false),
                    ContentFormat = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    ChangeDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SectionsCount = table.Column<int>(type: "int", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "AiActorExecutions",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AiActorId = table.Column<int>(type: "int", nullable: false),
                    TriggeringSubscriptionId = table.Column<int>(type: "int", nullable: true),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    QueriesAnalyzed = table.Column<int>(type: "int", nullable: false),
                    QueriesCreated = table.Column<int>(type: "int", nullable: false),
                    QueriesRefined = table.Column<int>(type: "int", nullable: false),
                    SubscriptionsCreated = table.Column<int>(type: "int", nullable: false),
                    NotificationsTriggered = table.Column<int>(type: "int", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DecisionSummary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ActionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DetailedAnalysis = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FindingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiActorPlanId = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiActorExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiActorExecutions_AiActorPlans_AiActorPlanId",
                        column: x => x.AiActorPlanId,
                        principalSchema: "semantico",
                        principalTable: "AiActorPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiActorExecutions_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "semantico",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiActorExecutions_Subscriptions_TriggeringSubscriptionId",
                        column: x => x.TriggeringSubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiActorConversations",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AiActorId = table.Column<int>(type: "int", nullable: false),
                    AiActorExecutionId = table.Column<int>(type: "int", nullable: true),
                    TurnNumber = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    MessageContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokensUsed = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiActorConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiActorConversations_AiActorExecutions_AiActorExecutionId",
                        column: x => x.AiActorExecutionId,
                        principalSchema: "semantico",
                        principalTable: "AiActorExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiActorConversations_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "semantico",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryStepChangeHistory",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryStepId = table.Column<int>(type: "int", nullable: false),
                    AiActorId = table.Column<int>(type: "int", nullable: true),
                    AiActorExecutionId = table.Column<int>(type: "int", nullable: true),
                    AiActorPlanId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PreviousSql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NewSql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangeSource = table.Column<int>(type: "int", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryStepChangeHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_AiActorExecutions_AiActorExecutionId",
                        column: x => x.AiActorExecutionId,
                        principalSchema: "semantico",
                        principalTable: "AiActorExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_AiActorPlans_AiActorPlanId",
                        column: x => x.AiActorPlanId,
                        principalSchema: "semantico",
                        principalTable: "AiActorPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "semantico",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_QuerySteps_QueryStepId",
                        column: x => x.QueryStepId,
                        principalSchema: "semantico",
                        principalTable: "QuerySteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_AiActorId",
                schema: "semantico",
                table: "Subscriptions",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_SubscriptionId",
                schema: "semantico",
                table: "QueryTasks",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Queries_AiActorId",
                schema: "semantico",
                table: "Queries",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Queries_IsLocked",
                schema: "semantico",
                table: "Queries",
                column: "IsLocked");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_AiActorExecutionId",
                schema: "semantico",
                table: "AiActorConversations",
                column: "AiActorExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_AiActorId",
                schema: "semantico",
                table: "AiActorConversations",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_AiActorId_TurnNumber",
                schema: "semantico",
                table: "AiActorConversations",
                columns: new[] { "AiActorId", "TurnNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_Timestamp",
                schema: "semantico",
                table: "AiActorConversations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_TurnNumber",
                schema: "semantico",
                table: "AiActorConversations",
                column: "TurnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_AiActorId",
                schema: "semantico",
                table: "AiActorExecutions",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_AiActorId_StartedAt",
                schema: "semantico",
                table: "AiActorExecutions",
                columns: new[] { "AiActorId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_AiActorPlanId",
                schema: "semantico",
                table: "AiActorExecutions",
                column: "AiActorPlanId",
                unique: true,
                filter: "[AiActorPlanId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_Phase",
                schema: "semantico",
                table: "AiActorExecutions",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_StartedAt",
                schema: "semantico",
                table: "AiActorExecutions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_TriggeringSubscriptionId",
                schema: "semantico",
                table: "AiActorExecutions",
                column: "TriggeringSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_AiActorId",
                schema: "semantico",
                table: "AiActorPlans",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_AiActorId_ProposedAt",
                schema: "semantico",
                table: "AiActorPlans",
                columns: new[] { "AiActorId", "ProposedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_AiActorId_Status",
                schema: "semantico",
                table: "AiActorPlans",
                columns: new[] { "AiActorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_ParentPlanId",
                schema: "semantico",
                table: "AiActorPlans",
                column: "ParentPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_ProposedAt",
                schema: "semantico",
                table: "AiActorPlans",
                column: "ProposedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_Status",
                schema: "semantico",
                table: "AiActorPlans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_ArchivedTime",
                schema: "semantico",
                table: "AiActors",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_DataSourceId",
                schema: "semantico",
                table: "AiActors",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_DataSourceId_Status",
                schema: "semantico",
                table: "AiActors",
                columns: new[] { "DataSourceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_Status",
                schema: "semantico",
                table: "AiActors",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_Status_ArchivedTime",
                schema: "semantico",
                table: "AiActors",
                columns: new[] { "Status", "ArchivedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAlertConfigurations_DataSourceId",
                schema: "semantico",
                table: "AiAlertConfigurations",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAlertConfigurations_Status",
                schema: "semantico",
                table: "AiAlertConfigurations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiAlertConfigurations_SubscriptionId",
                schema: "semantico",
                table: "AiAlertConfigurations",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationHistories_AiAlertConfigurationId",
                schema: "semantico",
                table: "AiConversationHistories",
                column: "AiAlertConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationHistories_Timestamp",
                schema: "semantico",
                table: "AiConversationHistories",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationHistories_TurnNumber",
                schema: "semantico",
                table: "AiConversationHistories",
                column: "TurnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_IsActive",
                schema: "semantico",
                table: "AiPromptTemplates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_OperationType",
                schema: "semantico",
                table: "AiPromptTemplates",
                column: "OperationType");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_DataSourceId",
                schema: "semantico",
                table: "AiUsageMetrics",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_OperationType",
                schema: "semantico",
                table: "AiUsageMetrics",
                column: "OperationType");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_Provider",
                schema: "semantico",
                table: "AiUsageMetrics",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_QueryId",
                schema: "semantico",
                table: "AiUsageMetrics",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_Timestamp",
                schema: "semantico",
                table: "AiUsageMetrics",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_UserId",
                schema: "semantico",
                table: "AiUsageMetrics",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyBaselines_ExecutionTime",
                schema: "semantico",
                table: "AnomalyBaselines",
                column: "ExecutionTime");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyBaselines_SubscriptionId_ExecutionTime",
                schema: "semantico",
                table: "AnomalyBaselines",
                columns: new[] { "SubscriptionId", "ExecutionTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyConfigs_Enabled",
                schema: "semantico",
                table: "AnomalyConfigs",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyConfigs_SubscriptionId",
                schema: "semantico",
                table: "AnomalyConfigs",
                column: "SubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_Acknowledged_DetectedTime",
                schema: "semantico",
                table: "AnomalyEvents",
                columns: new[] { "Acknowledged", "DetectedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_DetectedTime",
                schema: "semantico",
                table: "AnomalyEvents",
                column: "DetectedTime");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_NotificationId",
                schema: "semantico",
                table: "AnomalyEvents",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_SubscriptionId_DetectedTime",
                schema: "semantico",
                table: "AnomalyEvents",
                columns: new[] { "SubscriptionId", "DetectedTime" });

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
                name: "IX_DocumentationAgentRuns_DataSourceId",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                column: "DataSourceId");

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
                name: "IX_QueryStepChangeHistory_AiActorExecutionId",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "AiActorExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_AiActorId",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_AiActorPlanId",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "AiActorPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_ChangedAt",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_ChangeSource",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "ChangeSource");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_QueryStepId",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "QueryStepId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_QueryStepId_ChangedAt",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                columns: new[] { "QueryStepId", "ChangedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Queries_AiActors_AiActorId",
                schema: "semantico",
                table: "Queries",
                column: "AiActorId",
                principalSchema: "semantico",
                principalTable: "AiActors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_AiActors_AiActorId",
                schema: "semantico",
                table: "Subscriptions",
                column: "AiActorId",
                principalSchema: "semantico",
                principalTable: "AiActors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Queries_AiActors_AiActorId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_AiActors_AiActorId",
                schema: "semantico",
                table: "Subscriptions");

            migrationBuilder.DropTable(
                name: "AiActorConversations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiConversationHistories",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiPromptTemplates",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiUsageMetrics",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AnomalyBaselines",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AnomalyConfigs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AnomalyEvents",
                schema: "semantico");

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
                name: "QueryStepChangeHistory",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiAlertConfigurations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataSourceDocumentations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiActorExecutions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiActorPlans",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "AiActors",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_AiActorId",
                schema: "semantico",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_QueryTasks_SubscriptionId",
                schema: "semantico",
                table: "QueryTasks");

            migrationBuilder.DropIndex(
                name: "IX_Queries_AiActorId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropIndex(
                name: "IX_Queries_IsLocked",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "AiActorId",
                schema: "semantico",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "MinimumRowCount",
                schema: "semantico",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "NotificationTrigger",
                schema: "semantico",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "AiActorId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_SubscriptionId",
                schema: "semantico",
                table: "QueryTasks",
                column: "SubscriptionId",
                unique: true);
        }
    }
}
