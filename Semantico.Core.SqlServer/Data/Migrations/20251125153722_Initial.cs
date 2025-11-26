using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "semantico");

            migrationBuilder.CreateTable(
                name: "DataSources",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseEngineType = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Queries",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalQuery = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Queries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recipients",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Destination = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NotificationType = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseMetadata",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationJobs",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MigrationJobs_DataSources_DestinationDataSourceId",
                        column: x => x.DestinationDataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QueryParameters",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuerySteps",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuerySteps_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "semantico",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaxRows = table.Column<int>(type: "int", nullable: true),
                    IncludeAttachment = table.Column<bool>(type: "bit", nullable: false),
                    ResultAttachmentType = table.Column<int>(type: "int", nullable: true),
                    ShowQuery = table.Column<bool>(type: "bit", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    StoreResults = table.Column<bool>(type: "bit", nullable: false),
                    CreateTasks = table.Column<bool>(type: "bit", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "semantico",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ColumnMetadata",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "DatabaseMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndexMetadata",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "DatabaseMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationExecutions",
                schema: "semantico",
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
                    TransformationApplied = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                        principalSchema: "semantico",
                        principalTable: "MigrationExecutions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MigrationExecutions_MigrationJobs_MigrationJobId",
                        column: x => x.MigrationJobId,
                        principalSchema: "semantico",
                        principalTable: "MigrationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QueryStepParameters",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "QuerySteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueryExecutionHistory",
                schema: "semantico",
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
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryExecutionHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryExecutionHistory_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipientSubscription",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipientSubscription_Subscriptions_SubscriptionsId",
                        column: x => x.SubscriptionsId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionParameters",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                schema: "semantico",
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
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "semantico",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "semantico",
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
                        name: "FK_Notifications_QueryExecutionHistory_QueryExecutionHistoryId",
                        column: x => x.QueryExecutionHistoryId,
                        principalSchema: "semantico",
                        principalTable: "QueryExecutionHistory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Notifications_Recipients_RecipientId",
                        column: x => x.RecipientId,
                        principalSchema: "semantico",
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Notifications_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: "semantico",
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMetadata_DatabaseMetadataId",
                schema: "semantico",
                table: "ColumnMetadata",
                column: "DatabaseMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMetadata_DatabaseMetadataId_ColumnName",
                schema: "semantico",
                table: "ColumnMetadata",
                columns: new[] { "DatabaseMetadataId", "ColumnName" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseMetadata_DataSourceId",
                schema: "semantico",
                table: "DatabaseMetadata",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseMetadata_DataSourceId_SchemaName_TableName",
                schema: "semantico",
                table: "DatabaseMetadata",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseMetadata_LastRefreshed",
                schema: "semantico",
                table: "DatabaseMetadata",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_IndexMetadata_DatabaseMetadataId",
                schema: "semantico",
                table: "IndexMetadata",
                column: "DatabaseMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_MigrationJobId",
                schema: "semantico",
                table: "MigrationExecutions",
                column: "MigrationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_ParentExecutionId",
                schema: "semantico",
                table: "MigrationExecutions",
                column: "ParentExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_StartedAt",
                schema: "semantico",
                table: "MigrationExecutions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExecutions_Status_StartedAt",
                schema: "semantico",
                table: "MigrationExecutions",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_DataSourceId",
                schema: "semantico",
                table: "MigrationJobs",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_DestinationDataSourceId",
                schema: "semantico",
                table: "MigrationJobs",
                column: "DestinationDataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_IsEnabled_ArchivedTime",
                schema: "semantico",
                table: "MigrationJobs",
                columns: new[] { "IsEnabled", "ArchivedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_QueryExecutionHistoryId",
                schema: "semantico",
                table: "Notifications",
                column: "QueryExecutionHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientId",
                schema: "semantico",
                table: "Notifications",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TaskId",
                schema: "semantico",
                table: "Notifications",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryExecutionHistory_SubscriptionId",
                schema: "semantico",
                table: "QueryExecutionHistory",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryParameters_QueryId",
                schema: "semantico",
                table: "QueryParameters",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryStepParameters_QueryStepId",
                schema: "semantico",
                table: "QueryStepParameters",
                column: "QueryStepId");

            migrationBuilder.CreateIndex(
                name: "IX_QuerySteps_DataSourceId",
                schema: "semantico",
                table: "QuerySteps",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_QuerySteps_QueryId",
                schema: "semantico",
                table: "QuerySteps",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipientSubscription_SubscriptionsId",
                schema: "semantico",
                table: "RecipientSubscription",
                column: "SubscriptionsId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionParameters_SubscriptionId",
                schema: "semantico",
                table: "SubscriptionParameters",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_QueryId",
                schema: "semantico",
                table: "Subscriptions",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_Task_CreatedTime",
                schema: "semantico",
                table: "Tasks",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Resolved_CreatedTime",
                schema: "semantico",
                table: "Tasks",
                columns: new[] { "Resolved", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Task_SubscriptionId_Unique",
                schema: "semantico",
                table: "Tasks",
                column: "SubscriptionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnMetadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "IndexMetadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "MigrationExecutions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "QueryParameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "QueryStepParameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "RecipientSubscription",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "SubscriptionParameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DatabaseMetadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "MigrationJobs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "QueryExecutionHistory",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Tasks",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "QuerySteps",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Recipients",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Subscriptions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DataSources",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Queries",
                schema: "semantico");
        }
    }
}
