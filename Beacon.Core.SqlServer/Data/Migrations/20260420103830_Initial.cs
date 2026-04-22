using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "beacon");

            migrationBuilder.CreateTable(
                name: "AiPromptTemplates",
                schema: "beacon",
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
                name: "AppSettingHistory",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettingKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettingHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSettings",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityType = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
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
                name: "DataProtectionKeys",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataSources",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DataSourceType = table.Column<int>(type: "int", nullable: false),
                    EncryptedConnectionData = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseEngineType = table.Column<int>(type: "int", nullable: true),
                    MetadataLoadingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MetadataMaxTables = table.Column<int>(type: "int", nullable: false),
                    MetadataMaxColumnsPerTable = table.Column<int>(type: "int", nullable: false),
                    MetadataLoadTableNamesOnly = table.Column<bool>(type: "bit", nullable: false),
                    MetadataExcludeSchemas = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetadataIncludeSchemas = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpDocumentationPatches",
                schema: "beacon",
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
                schema: "beacon",
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
                schema: "beacon",
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

            migrationBuilder.CreateTable(
                name: "McpSettings",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AskSystemPrompt = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    GlobalInstruction = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    GetContextDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SearchDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    QueryDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    GetDocumentationDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AskDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MaxRowLimit = table.Column<int>(type: "int", nullable: false),
                    EnforceReadOnly = table.Column<bool>(type: "bit", nullable: false),
                    EnablePiiDetection = table.Column<bool>(type: "bit", nullable: false),
                    CustomPiiPatterns = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EnableLearning = table.Column<bool>(type: "bit", nullable: false),
                    LearningAutoApproveThreshold = table.Column<double>(type: "float", nullable: false),
                    LearningInjectionBudgetChars = table.Column<int>(type: "int", nullable: false),
                    LearningSignalRetentionDays = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueryFolders",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ParentFolderId = table.Column<int>(type: "int", nullable: true),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryFolders_QueryFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalSchema: "beacon",
                        principalTable: "QueryFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Recipients",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Destination = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NotificationType = table.Column<int>(type: "int", nullable: false),
                    HeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BodyTemplate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSystemRole = table.Column<bool>(type: "bit", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IdentityProvider = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsInternalUser = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PasswordSalt = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSuperAdmin = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
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
                name: "AiActors",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseMetadata",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastRefreshed = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseMetadata_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataContracts",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DataQualityScores",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManualQueryExecutionLogs",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QueryText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    ExecutionTimeMs = table.Column<double>(type: "float", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    ExecutionContext = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualQueryExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualQueryExecutionLogs_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MigrationJobs",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    QueryText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DestinationDataSourceId = table.Column<int>(type: "int", nullable: false),
                    DestinationTable = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Schedule = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    MaxRetries = table.Column<int>(type: "int", nullable: false),
                    TimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    ValidateBeforeExecution = table.Column<bool>(type: "bit", nullable: false),
                    TransformationScript = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationJobs_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MigrationJobs_DataSources_DestinationDataSourceId",
                        column: x => x.DestinationDataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GitHubRepositories",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDataSources",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectDataSources_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentations",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeyCredentials",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowedProjectIds = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
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
                        principalSchema: "beacon",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    AssignedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "beacon",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "beacon",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiActorPlans",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "AiActorPlans",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AiActorPlans_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "beacon",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ColumnMetadata",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseMetadataId = table.Column<int>(type: "int", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsNullable = table.Column<bool>(type: "bit", nullable: false),
                    IsPrimaryKey = table.Column<bool>(type: "bit", nullable: false),
                    IsForeignKey = table.Column<bool>(type: "bit", nullable: false),
                    OrdinalPosition = table.Column<int>(type: "int", nullable: false),
                    ForeignKeyTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ForeignKeyColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ColumnMetadata_DatabaseMetadata_DatabaseMetadataId",
                        column: x => x.DatabaseMetadataId,
                        principalSchema: "beacon",
                        principalTable: "DatabaseMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndexMetadata",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseMetadataId = table.Column<int>(type: "int", nullable: false),
                    IndexName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsUnique = table.Column<bool>(type: "bit", nullable: false),
                    IsPrimaryKey = table.Column<bool>(type: "bit", nullable: false),
                    Columns = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndexMetadata_DatabaseMetadata_DatabaseMetadataId",
                        column: x => x.DatabaseMetadataId,
                        principalSchema: "beacon",
                        principalTable: "DatabaseMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataContractRecipient",
                schema: "beacon",
                columns: table => new
                {
                    DataContractsId = table.Column<int>(type: "int", nullable: false),
                    RecipientsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataContractRecipient", x => new { x.DataContractsId, x.RecipientsId });
                    table.ForeignKey(
                        name: "FK_DataContractRecipient_DataContracts_DataContractsId",
                        column: x => x.DataContractsId,
                        principalSchema: "beacon",
                        principalTable: "DataContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataContractRecipient_Recipients_RecipientsId",
                        column: x => x.RecipientsId,
                        principalSchema: "beacon",
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataContractRules",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataQualityEvaluations",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MigrationExecutions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MigrationJobId = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SourceRowsRead = table.Column<int>(type: "int", nullable: false),
                    DestinationRowsWritten = table.Column<int>(type: "int", nullable: false),
                    RowsSkipped = table.Column<int>(type: "int", nullable: false),
                    RowsFailed = table.Column<int>(type: "int", nullable: false),
                    ExecutedQuery = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QueryParameters = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RetryAttempt = table.Column<int>(type: "int", nullable: false),
                    ParentExecutionId = table.Column<int>(type: "int", nullable: true),
                    EstimatedTotalRows = table.Column<int>(type: "int", nullable: true),
                    ProcessedRows = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationExecutions_MigrationExecutions_ParentExecutionId",
                        column: x => x.ParentExecutionId,
                        principalSchema: "beacon",
                        principalTable: "MigrationExecutions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MigrationExecutions_MigrationJobs_MigrationJobId",
                        column: x => x.MigrationJobId,
                        principalSchema: "beacon",
                        principalTable: "MigrationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CodeReferences",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "GitHubRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentationSections",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "ProjectDocumentations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "DataQualityRuleResults",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "DataContractRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataQualityRuleResults_DataQualityEvaluations_DataQualityEvaluationId",
                        column: x => x.DataQualityEvaluationId,
                        principalSchema: "beacon",
                        principalTable: "DataQualityEvaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McpAuditLogs",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Tool = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Parameters = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
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
                        principalSchema: "beacon",
                        principalTable: "McpSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_McpAuditLogs_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "beacon",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiActorConversations",
                schema: "beacon",
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
                        name: "FK_AiActorConversations_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "beacon",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiActorExecutions",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "AiActorPlans",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AiActorExecutions_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "beacon",
                        principalTable: "AiActors",
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
                });

            migrationBuilder.CreateTable(
                name: "AnomalyBaselines",
                schema: "beacon",
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
                });

            migrationBuilder.CreateTable(
                name: "AnomalyConfigs",
                schema: "beacon",
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
                });

            migrationBuilder.CreateTable(
                name: "AnomalyEvents",
                schema: "beacon",
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
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryExecutionHistoryId = table.Column<int>(type: "int", nullable: false),
                    RecipientId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Results = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Recipients_RecipientId",
                        column: x => x.RecipientId,
                        principalSchema: "beacon",
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Queries",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FolderId = table.Column<int>(type: "int", nullable: true),
                    FinalQuery = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiActorId = table.Column<int>(type: "int", nullable: true),
                    IsLocked = table.Column<bool>(type: "bit", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActiveVersionId = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Queries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Queries_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "beacon",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Queries_QueryFolders_FolderId",
                        column: x => x.FolderId,
                        principalSchema: "beacon",
                        principalTable: "QueryFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QueryParameters",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Placeholder = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryParameters_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuerySteps",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    StepOrder = table.Column<int>(type: "int", nullable: false),
                    SqlValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuerySteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuerySteps_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuerySteps_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryVersions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalQuery = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StepsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ChangeSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryVersions_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaxRows = table.Column<int>(type: "int", nullable: true),
                    MinimumRowCount = table.Column<int>(type: "int", nullable: true),
                    IncludeAttachment = table.Column<bool>(type: "bit", nullable: false),
                    ResultAttachmentType = table.Column<int>(type: "int", nullable: true),
                    ShowQuery = table.Column<bool>(type: "bit", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    StoreResults = table.Column<bool>(type: "bit", nullable: false),
                    CreateTasks = table.Column<bool>(type: "bit", nullable: false),
                    NotificationTrigger = table.Column<int>(type: "int", nullable: false),
                    AiActorId = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "beacon",
                        principalTable: "AiActors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryStepChangeHistory",
                schema: "beacon",
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
                        principalSchema: "beacon",
                        principalTable: "AiActorExecutions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_AiActorPlans_AiActorPlanId",
                        column: x => x.AiActorPlanId,
                        principalSchema: "beacon",
                        principalTable: "AiActorPlans",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_AiActors_AiActorId",
                        column: x => x.AiActorId,
                        principalSchema: "beacon",
                        principalTable: "AiActors",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QueryStepChangeHistory_QuerySteps_QueryStepId",
                        column: x => x.QueryStepId,
                        principalSchema: "beacon",
                        principalTable: "QuerySteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryStepParameters",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryStepId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Placeholder = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryStepParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryStepParameters_QuerySteps_QueryStepId",
                        column: x => x.QueryStepId,
                        principalSchema: "beacon",
                        principalTable: "QuerySteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryApprovalRequests",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    QueryVersionId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReviewedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangeSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryApprovalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryApprovalRequests_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QueryApprovalRequests_QueryVersions_QueryVersionId",
                        column: x => x.QueryVersionId,
                        principalSchema: "beacon",
                        principalTable: "QueryVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QueryExecutionHistory",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    CompiledSql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NotificationStatus = table.Column<int>(type: "int", nullable: false),
                    ExecutionTimeMs = table.Column<double>(type: "float", nullable: false),
                    Results = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryExecutionHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryExecutionHistory_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "beacon",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryTasks",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    LatestResultCount = table.Column<int>(type: "int", nullable: false),
                    LastNotificationAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Resolved = table.Column<bool>(type: "bit", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryTasks_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "beacon",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecipientSubscription",
                schema: "beacon",
                columns: table => new
                {
                    RecipientsId = table.Column<int>(type: "int", nullable: false),
                    SubscriptionsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientSubscription", x => new { x.RecipientsId, x.SubscriptionsId });
                    table.ForeignKey(
                        name: "FK_RecipientSubscription_Recipients_RecipientsId",
                        column: x => x.RecipientsId,
                        principalSchema: "beacon",
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipientSubscription_Subscriptions_SubscriptionsId",
                        column: x => x.SubscriptionsId,
                        principalSchema: "beacon",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionParameters",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    QueryPlaceholder = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionParameters_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "beacon",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_AiActorExecutionId",
                schema: "beacon",
                table: "AiActorConversations",
                column: "AiActorExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_AiActorId_TurnNumber",
                schema: "beacon",
                table: "AiActorConversations",
                columns: new[] { "AiActorId", "TurnNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_Timestamp",
                schema: "beacon",
                table: "AiActorConversations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_TurnNumber",
                schema: "beacon",
                table: "AiActorConversations",
                column: "TurnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_AiActorId_StartedAt",
                schema: "beacon",
                table: "AiActorExecutions",
                columns: new[] { "AiActorId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_AiActorPlanId",
                schema: "beacon",
                table: "AiActorExecutions",
                column: "AiActorPlanId",
                unique: true,
                filter: "[AiActorPlanId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_Phase",
                schema: "beacon",
                table: "AiActorExecutions",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_StartedAt",
                schema: "beacon",
                table: "AiActorExecutions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_TriggeringSubscriptionId",
                schema: "beacon",
                table: "AiActorExecutions",
                column: "TriggeringSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_AiActorId_ProposedAt",
                schema: "beacon",
                table: "AiActorPlans",
                columns: new[] { "AiActorId", "ProposedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_AiActorId_Status",
                schema: "beacon",
                table: "AiActorPlans",
                columns: new[] { "AiActorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_ParentPlanId",
                schema: "beacon",
                table: "AiActorPlans",
                column: "ParentPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_ProposedAt",
                schema: "beacon",
                table: "AiActorPlans",
                column: "ProposedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_Status",
                schema: "beacon",
                table: "AiActorPlans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_ArchivedTime",
                schema: "beacon",
                table: "AiActors",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_DataSourceId_Status",
                schema: "beacon",
                table: "AiActors",
                columns: new[] { "DataSourceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_Status",
                schema: "beacon",
                table: "AiActors",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_Status_ArchivedTime",
                schema: "beacon",
                table: "AiActors",
                columns: new[] { "Status", "ArchivedTime" });

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
                name: "IX_AiPromptTemplates_IsActive",
                schema: "beacon",
                table: "AiPromptTemplates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_OperationType",
                schema: "beacon",
                table: "AiPromptTemplates",
                column: "OperationType");

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

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyBaselines_ExecutionTime",
                schema: "beacon",
                table: "AnomalyBaselines",
                column: "ExecutionTime");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyBaselines_SubscriptionId_ExecutionTime",
                schema: "beacon",
                table: "AnomalyBaselines",
                columns: new[] { "SubscriptionId", "ExecutionTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyConfigs_Enabled",
                schema: "beacon",
                table: "AnomalyConfigs",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyConfigs_SubscriptionId",
                schema: "beacon",
                table: "AnomalyConfigs",
                column: "SubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_Acknowledged_DetectedTime",
                schema: "beacon",
                table: "AnomalyEvents",
                columns: new[] { "Acknowledged", "DetectedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_DetectedTime",
                schema: "beacon",
                table: "AnomalyEvents",
                column: "DetectedTime");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_NotificationId",
                schema: "beacon",
                table: "AnomalyEvents",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_SubscriptionId_DetectedTime",
                schema: "beacon",
                table: "AnomalyEvents",
                columns: new[] { "SubscriptionId", "DetectedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_IsRevoked",
                schema: "beacon",
                table: "ApiKeyCredentials",
                column: "IsRevoked");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_KeyHash",
                schema: "beacon",
                table: "ApiKeyCredentials",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_KeyPrefix",
                schema: "beacon",
                table: "ApiKeyCredentials",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_UserId",
                schema: "beacon",
                table: "ApiKeyCredentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettingHistory_ChangedAt",
                schema: "beacon",
                table: "AppSettingHistory",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettingHistory_SettingKey",
                schema: "beacon",
                table: "AppSettingHistory",
                column: "SettingKey");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Category",
                schema: "beacon",
                table: "AppSettings",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Key",
                schema: "beacon",
                table: "AppSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CodeReferences_GitHubRepositoryId",
                schema: "beacon",
                table: "CodeReferences",
                column: "GitHubRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeReferences_ReferenceType",
                schema: "beacon",
                table: "CodeReferences",
                column: "ReferenceType");

            migrationBuilder.CreateIndex(
                name: "IX_CodeReferences_SchemaName_TableName",
                schema: "beacon",
                table: "CodeReferences",
                columns: new[] { "SchemaName", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMetadata_DatabaseMetadataId",
                schema: "beacon",
                table: "ColumnMetadata",
                column: "DatabaseMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMetadata_DatabaseMetadataId_ColumnName",
                schema: "beacon",
                table: "ColumnMetadata",
                columns: new[] { "DatabaseMetadataId", "ColumnName" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_CreatedTime",
                schema: "beacon",
                table: "Comments",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_EntityType_EntityId",
                schema: "beacon",
                table: "Comments",
                columns: new[] { "EntityType", "EntityId" });

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
                name: "IX_DatabaseMetadata_DataSourceId",
                schema: "beacon",
                table: "DatabaseMetadata",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseMetadata_DataSourceId_SchemaName_TableName",
                schema: "beacon",
                table: "DatabaseMetadata",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseMetadata_LastRefreshed",
                schema: "beacon",
                table: "DatabaseMetadata",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_DataContractRecipient_RecipientsId",
                schema: "beacon",
                table: "DataContractRecipient",
                column: "RecipientsId");

            migrationBuilder.CreateIndex(
                name: "IX_DataContractRules_DataContractId",
                schema: "beacon",
                table: "DataContractRules",
                column: "DataContractId");

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_ArchivedTime",
                schema: "beacon",
                table: "DataContracts",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_DataSourceId",
                schema: "beacon",
                table: "DataContracts",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_DataSourceId_SchemaName_TableName",
                schema: "beacon",
                table: "DataContracts",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_DataContracts_IsEnabled",
                schema: "beacon",
                table: "DataContracts",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityEvaluations_CreatedTime",
                schema: "beacon",
                table: "DataQualityEvaluations",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityEvaluations_DataContractId",
                schema: "beacon",
                table: "DataQualityEvaluations",
                column: "DataContractId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityRuleResults_DataContractRuleId",
                schema: "beacon",
                table: "DataQualityRuleResults",
                column: "DataContractRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityRuleResults_DataQualityEvaluationId",
                schema: "beacon",
                table: "DataQualityRuleResults",
                column: "DataQualityEvaluationId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityScores_DataSourceId_SchemaName_TableName",
                schema: "beacon",
                table: "DataQualityScores",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityScores_EvaluatedAt",
                schema: "beacon",
                table: "DataQualityScores",
                column: "EvaluatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubRepositories_ProjectId",
                schema: "beacon",
                table: "GitHubRepositories",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubRepositories_ScanStatus",
                schema: "beacon",
                table: "GitHubRepositories",
                column: "ScanStatus");

            migrationBuilder.CreateIndex(
                name: "IX_IndexMetadata_DatabaseMetadataId",
                schema: "beacon",
                table: "IndexMetadata",
                column: "DatabaseMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_CreatedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_DataSourceId",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_DataSourceId_CreatedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                columns: new[] { "DataSourceId", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_ExecutionContext",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                column: "ExecutionContext");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_UserId",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_UserId_CreatedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                columns: new[] { "UserId", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_CreatedTime",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_SessionId",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_Tool",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "Tool");

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditLogs_UserId",
                schema: "beacon",
                table: "McpAuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_McpDocumentationPatches_DataSourceId",
                schema: "beacon",
                table: "McpDocumentationPatches",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_McpDocumentationPatches_ProjectId_Status",
                schema: "beacon",
                table: "McpDocumentationPatches",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_McpLearnedPatterns_DataSourceId_Status_TableName",
                schema: "beacon",
                table: "McpLearnedPatterns",
                columns: new[] { "DataSourceId", "Status", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_McpLearnedPatterns_ProjectId",
                schema: "beacon",
                table: "McpLearnedPatterns",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_CreatedTime",
                schema: "beacon",
                table: "McpQuerySignals",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_DataSourceId",
                schema: "beacon",
                table: "McpQuerySignals",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_IsSuccessful",
                schema: "beacon",
                table: "McpQuerySignals",
                column: "IsSuccessful");

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_ProjectId",
                schema: "beacon",
                table: "McpQuerySignals",
                column: "ProjectId");

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
                name: "IX_MigrationExecutions_MigrationJobId",
                schema: "beacon",
                table: "MigrationExecutions",
                column: "MigrationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_ParentExecutionId",
                schema: "beacon",
                table: "MigrationExecutions",
                column: "ParentExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_StartedAt",
                schema: "beacon",
                table: "MigrationExecutions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_Status_StartedAt",
                schema: "beacon",
                table: "MigrationExecutions",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_DataSourceId",
                schema: "beacon",
                table: "MigrationJobs",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_DestinationDataSourceId",
                schema: "beacon",
                table: "MigrationJobs",
                column: "DestinationDataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_IsEnabled_ArchivedTime",
                schema: "beacon",
                table: "MigrationJobs",
                columns: new[] { "IsEnabled", "ArchivedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_QueryExecutionHistoryId",
                schema: "beacon",
                table: "Notifications",
                column: "QueryExecutionHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientId",
                schema: "beacon",
                table: "Notifications",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TaskId",
                schema: "beacon",
                table: "Notifications",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDataSources_DataSourceId",
                schema: "beacon",
                table: "ProjectDataSources",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDataSources_ProjectId_DataSourceId",
                schema: "beacon",
                table: "ProjectDataSources",
                columns: new[] { "ProjectId", "DataSourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentations_GeneratedAt",
                schema: "beacon",
                table: "ProjectDocumentations",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentations_ProjectId",
                schema: "beacon",
                table: "ProjectDocumentations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentationSections_ProjectDocumentationId",
                schema: "beacon",
                table: "ProjectDocumentationSections",
                column: "ProjectDocumentationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentationSections_SectionType",
                schema: "beacon",
                table: "ProjectDocumentationSections",
                column: "SectionType");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                schema: "beacon",
                table: "Projects",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Queries_ActiveVersionId",
                schema: "beacon",
                table: "Queries",
                column: "ActiveVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Queries_AiActorId",
                schema: "beacon",
                table: "Queries",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Queries_FolderId",
                schema: "beacon",
                table: "Queries",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Queries_IsLocked",
                schema: "beacon",
                table: "Queries",
                column: "IsLocked");

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_QueryId_Status",
                schema: "beacon",
                table: "QueryApprovalRequests",
                columns: new[] { "QueryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_QueryVersionId",
                schema: "beacon",
                table: "QueryApprovalRequests",
                column: "QueryVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_Status",
                schema: "beacon",
                table: "QueryApprovalRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_Status_CreatedTime",
                schema: "beacon",
                table: "QueryApprovalRequests",
                columns: new[] { "Status", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryExecutionHistory_SubscriptionId",
                schema: "beacon",
                table: "QueryExecutionHistory",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ArchivedTime",
                schema: "beacon",
                table: "QueryFolders",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ParentFolderId",
                schema: "beacon",
                table: "QueryFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ParentFolderId_Name",
                schema: "beacon",
                table: "QueryFolders",
                columns: new[] { "ParentFolderId", "Name" },
                unique: true,
                filter: "[ParentFolderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ParentFolderId_SortOrder",
                schema: "beacon",
                table: "QueryFolders",
                columns: new[] { "ParentFolderId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_Path",
                schema: "beacon",
                table: "QueryFolders",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_QueryParameters_QueryId",
                schema: "beacon",
                table: "QueryParameters",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_AiActorExecutionId",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                column: "AiActorExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_AiActorId",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_AiActorPlanId",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                column: "AiActorPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_ChangedAt",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_ChangeSource",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                column: "ChangeSource");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_QueryStepId_ChangedAt",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                columns: new[] { "QueryStepId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepParameters_QueryStepId",
                schema: "beacon",
                table: "QueryStepParameters",
                column: "QueryStepId");

            migrationBuilder.CreateIndex(
                name: "IX_QuerySteps_DataSourceId",
                schema: "beacon",
                table: "QuerySteps",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_QuerySteps_QueryId",
                schema: "beacon",
                table: "QuerySteps",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_CreatedTime",
                schema: "beacon",
                table: "QueryTasks",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_Resolved_CreatedTime",
                schema: "beacon",
                table: "QueryTasks",
                columns: new[] { "Resolved", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_SubscriptionId",
                schema: "beacon",
                table: "QueryTasks",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryVersions_QueryId_Status",
                schema: "beacon",
                table: "QueryVersions",
                columns: new[] { "QueryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryVersions_QueryId_VersionNumber",
                schema: "beacon",
                table: "QueryVersions",
                columns: new[] { "QueryId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipientSubscription_SubscriptionsId",
                schema: "beacon",
                table: "RecipientSubscription",
                column: "SubscriptionsId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                schema: "beacon",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionParameters_SubscriptionId",
                schema: "beacon",
                table: "SubscriptionParameters",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_AiActorId",
                schema: "beacon",
                table: "Subscriptions",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_QueryId",
                schema: "beacon",
                table: "Subscriptions",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_AssignedAt",
                schema: "beacon",
                table: "UserRoles",
                column: "AssignedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                schema: "beacon",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId",
                schema: "beacon",
                table: "UserRoles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                schema: "beacon",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ArchivedTime",
                schema: "beacon",
                table: "Users",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "beacon",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalId",
                schema: "beacon",
                table: "Users",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IdentityProvider_ExternalId",
                schema: "beacon",
                table: "Users",
                columns: new[] { "IdentityProvider", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsEnabled",
                schema: "beacon",
                table: "Users",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsInternalUser",
                schema: "beacon",
                table: "Users",
                column: "IsInternalUser");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsSuperAdmin",
                schema: "beacon",
                table: "Users",
                column: "IsSuperAdmin");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName_ArchivedTime",
                schema: "beacon",
                table: "Users",
                columns: new[] { "UserName", "ArchivedTime" });

            migrationBuilder.AddForeignKey(
                name: "FK_AiActorConversations_AiActorExecutions_AiActorExecutionId",
                schema: "beacon",
                table: "AiActorConversations",
                column: "AiActorExecutionId",
                principalSchema: "beacon",
                principalTable: "AiActorExecutions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AiActorExecutions_Subscriptions_TriggeringSubscriptionId",
                schema: "beacon",
                table: "AiActorExecutions",
                column: "TriggeringSubscriptionId",
                principalSchema: "beacon",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AiAlertConfigurations_Subscriptions_SubscriptionId",
                schema: "beacon",
                table: "AiAlertConfigurations",
                column: "SubscriptionId",
                principalSchema: "beacon",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AiUsageMetrics_Queries_QueryId",
                schema: "beacon",
                table: "AiUsageMetrics",
                column: "QueryId",
                principalSchema: "beacon",
                principalTable: "Queries",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AnomalyBaselines_Subscriptions_SubscriptionId",
                schema: "beacon",
                table: "AnomalyBaselines",
                column: "SubscriptionId",
                principalSchema: "beacon",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AnomalyConfigs_Subscriptions_SubscriptionId",
                schema: "beacon",
                table: "AnomalyConfigs",
                column: "SubscriptionId",
                principalSchema: "beacon",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AnomalyEvents_Notifications_NotificationId",
                schema: "beacon",
                table: "AnomalyEvents",
                column: "NotificationId",
                principalSchema: "beacon",
                principalTable: "Notifications",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AnomalyEvents_Subscriptions_SubscriptionId",
                schema: "beacon",
                table: "AnomalyEvents",
                column: "SubscriptionId",
                principalSchema: "beacon",
                principalTable: "Subscriptions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_QueryExecutionHistory_QueryExecutionHistoryId",
                schema: "beacon",
                table: "Notifications",
                column: "QueryExecutionHistoryId",
                principalSchema: "beacon",
                principalTable: "QueryExecutionHistory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_QueryTasks_TaskId",
                schema: "beacon",
                table: "Notifications",
                column: "TaskId",
                principalSchema: "beacon",
                principalTable: "QueryTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Queries_QueryVersions_ActiveVersionId",
                schema: "beacon",
                table: "Queries",
                column: "ActiveVersionId",
                principalSchema: "beacon",
                principalTable: "QueryVersions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Queries_AiActors_AiActorId",
                schema: "beacon",
                table: "Queries");

            migrationBuilder.DropForeignKey(
                name: "FK_QueryVersions_Queries_QueryId",
                schema: "beacon",
                table: "QueryVersions");

            migrationBuilder.DropTable(
                name: "AiActorConversations",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiConversationHistories",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiPromptTemplates",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiUsageMetrics",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AnomalyBaselines",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AnomalyConfigs",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AnomalyEvents",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AppSettingHistory",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AppSettings",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "CodeReferences",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ColumnMetadata",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Comments",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DashboardPermissions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DashboardWidgets",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataContractRecipient",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataQualityRuleResults",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataQualityScores",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "IndexMetadata",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ManualQueryExecutionLogs",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpAuditLogs",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpDocumentationPatches",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpLearnedPatterns",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpQuerySignals",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpSettings",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "MigrationExecutions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ProjectDataSources",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ProjectDocumentationSections",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryApprovalRequests",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryParameters",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryStepChangeHistory",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryStepParameters",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "RecipientSubscription",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "SubscriptionParameters",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiAlertConfigurations",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "GitHubRepositories",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Dashboards",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataContractRules",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataQualityEvaluations",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DatabaseMetadata",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpSessions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "MigrationJobs",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ProjectDocumentations",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiActorExecutions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QuerySteps",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryExecutionHistory",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryTasks",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Recipients",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataContracts",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ApiKeyCredentials",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiActorPlans",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Subscriptions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AiActors",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "DataSources",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "Queries",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryFolders",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "QueryVersions",
                schema: "beacon");
        }
    }
}
