using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using System.Linq.Expressions;

namespace Beacon.Core.Data;

public abstract partial class BeaconContext : DbContext, IDataProtectionKeyContext
{
    private readonly string _defaultSchema;
    protected BeaconContext(DbContextOptions options, string defaultSchema = "beacon")
       : base(options)
    {
        _defaultSchema = defaultSchema;
    }
    protected string DefaultSchema => _defaultSchema;

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<SubscriptionParameter> SubscriptionParameters => Set<SubscriptionParameter>();

    public DbSet<Query> Queries => Set<Query>();

    public DbSet<QueryFolder> QueryFolders => Set<QueryFolder>();

    public DbSet<QueryParameter> QueryParameters => Set<QueryParameter>();

    public DbSet<QueryStep> QuerySteps => Set<QueryStep>();

    public DbSet<QueryStepParameter> QueryStepParameters => Set<QueryStepParameter>();

    public DbSet<DataSource> DataSources => Set<DataSource>();

    public DbSet<QueryExecutionHistory> QueryExecutionHistory => Set<QueryExecutionHistory>();

    public DbSet<Recipient> Recipients => Set<Recipient>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<QueryTask> QueryTasks => Set<QueryTask>();

    public DbSet<MigrationJob> MigrationJobs => Set<MigrationJob>();

    public DbSet<MigrationExecutionHistory> MigrationExecutions => Set<MigrationExecutionHistory>();

    public DbSet<DatabaseMetadata> DatabaseMetadata => Set<DatabaseMetadata>();

    public DbSet<ColumnMetadata> ColumnMetadata => Set<ColumnMetadata>();

    public DbSet<IndexMetadata> IndexMetadata => Set<IndexMetadata>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<AnomalyConfig> AnomalyConfigs => Set<AnomalyConfig>();

    public DbSet<AnomalyBaseline> AnomalyBaselines => Set<AnomalyBaseline>();

    public DbSet<AnomalyEvent> AnomalyEvents => Set<AnomalyEvent>();

    public DbSet<AiUsageMetrics> AiUsageMetrics => Set<AiUsageMetrics>();

    public DbSet<AiPromptTemplate> AiPromptTemplates => Set<AiPromptTemplate>();

    public DbSet<AiAlertConfiguration> AiAlertConfigurations => Set<AiAlertConfiguration>();

    public DbSet<AiConversationHistory> AiConversationHistories => Set<AiConversationHistory>();

    public DbSet<AiActor> AiActors => Set<AiActor>();

    public DbSet<AiActorExecution> AiActorExecutions => Set<AiActorExecution>();

    public DbSet<AiActorConversation> AiActorConversations => Set<AiActorConversation>();

    public DbSet<AiActorPlan> AiActorPlans => Set<AiActorPlan>();

    public DbSet<QueryStepChangeHistory> QueryStepChangeHistory => Set<QueryStepChangeHistory>();

    public DbSet<ManualQueryExecutionLog> ManualQueryExecutionLogs => Set<ManualQueryExecutionLog>();

    // User Management
    public DbSet<BeaconUser> Users => Set<BeaconUser>();

    public DbSet<BeaconRole> Roles => Set<BeaconRole>();

    public DbSet<BeaconUserRole> UserRoles => Set<BeaconUserRole>();

    // Query Versioning
    public DbSet<QueryVersion> QueryVersions => Set<QueryVersion>();

    // Approval Workflow
    public DbSet<QueryApprovalRequest> QueryApprovalRequests => Set<QueryApprovalRequest>();

    // App Settings
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<AppSettingHistory> AppSettingHistory => Set<AppSettingHistory>();

    // Custom Dashboards
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    public DbSet<DashboardWidget> DashboardWidgets => Set<DashboardWidget>();

    public DbSet<DashboardPermission> DashboardPermissions => Set<DashboardPermission>();

    // Data Quality
    public DbSet<DataContract> DataContracts => Set<DataContract>();

    public DbSet<DataContractRule> DataContractRules => Set<DataContractRule>();

    public DbSet<DataQualityEvaluation> DataQualityEvaluations => Set<DataQualityEvaluation>();

    public DbSet<DataQualityRuleResult> DataQualityRuleResults => Set<DataQualityRuleResult>();

    public DbSet<DataQualityScore> DataQualityScores => Set<DataQualityScore>();

    // Data Protection Keys (for cookie authentication)
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // Projects & Knowledge Graph
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectDataSource> ProjectDataSources => Set<ProjectDataSource>();

    public DbSet<GitHubRepository> GitHubRepositories => Set<GitHubRepository>();

    public DbSet<CodeReference> CodeReferences => Set<CodeReference>();

    public DbSet<ProjectDocumentation> ProjectDocumentations => Set<ProjectDocumentation>();

    public DbSet<ProjectDocumentationSection> ProjectDocumentationSections => Set<ProjectDocumentationSection>();

    // API Keys & MCP
    public DbSet<ApiKeyCredential> ApiKeyCredentials => Set<ApiKeyCredential>();

    public DbSet<McpSession> McpSessions => Set<McpSession>();

    public DbSet<McpAuditLog> McpAuditLogs => Set<McpAuditLog>();

    public DbSet<McpSettings> McpSettings => Set<McpSettings>();

    // MCP Learning
    public DbSet<McpQuerySignal> McpQuerySignals => Set<McpQuerySignal>();
    public DbSet<McpLearnedPattern> McpLearnedPatterns => Set<McpLearnedPattern>();
    public DbSet<McpDocumentationPatch> McpDocumentationPatches => Set<McpDocumentationPatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Set default schema for all entities
        modelBuilder.HasDefaultSchema(DefaultSchema);

        SetSoftDeleteQueryFilter(modelBuilder);
        ConfigureMigrationEntities(modelBuilder);
        ConfigureMetadataEntities(modelBuilder);
        ConfigureTaskEntity(modelBuilder);
        ConfigureCommentEntity(modelBuilder);
        ConfigureAnomalyEntities(modelBuilder);
        ConfigureAiEntities(modelBuilder);
        ConfigureAiActorEntities(modelBuilder);
        ConfigureQueryFolderEntities(modelBuilder);
        ConfigureManualQueryExecutionLogEntity(modelBuilder);
        ConfigureUserManagementEntities(modelBuilder);
        ConfigureAppSettingEntities(modelBuilder);
        ConfigureQueryVersionEntities(modelBuilder);
        ConfigureApprovalEntities(modelBuilder);
        ConfigureDashboardEntities(modelBuilder);
        ConfigureDataQualityEntities(modelBuilder);
        ConfigureProjectEntities(modelBuilder);
        ConfigureApiKeyEntities(modelBuilder);
        ConfigureMcpEntities(modelBuilder);
        ConfigureMcpLearningEntities(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    protected void SetSoftDeleteQueryFilter(ModelBuilder modelBuilder)
    {
        var softDeleteEntities = typeof(ArchivableBaseEntity).Assembly.GetTypes()
            .Where(x => typeof(ArchivableBaseEntity).IsAssignableFrom(x))
            .Where(x => x.IsClass)
            .Where(x => !x.IsAbstract);

        foreach (var softDeleteEntity in softDeleteEntities)
        {
            modelBuilder.Entity(softDeleteEntity)
                .HasQueryFilter(GenerateQueryFilterLambdaExpression(softDeleteEntity));
        }

        static LambdaExpression GenerateQueryFilterLambdaExpression(Type type)
        {
            // we generate:  x => x.ArchivedTime == null

            // x =>
            var parameter = Expression.Parameter(type, "x");

            // null
            var falseConstant = Expression.Constant(null);

            // x.ArchiveDate
            var propertyAccess = Expression.PropertyOrField(parameter, nameof(ArchivableBaseEntity.ArchivedTime));

            // e.ArchiveDate == null
            var equalExpression = Expression.Equal(propertyAccess, falseConstant);

            // x => e.ArchiveDate == null
            var lambda = Expression.Lambda(equalExpression, parameter);

            return lambda;
        }
    }

    protected static void ConfigureMigrationEntities(ModelBuilder modelBuilder)
    {
        // MigrationJob configuration
        modelBuilder.Entity<MigrationJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.QueryText).IsRequired();
            entity.Property(e => e.DestinationTable).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Schedule).HasMaxLength(50);
            
            // Relationships
            entity.HasOne(e => e.DataSource)
                  .WithMany()
                  .HasForeignKey(e => e.DataSourceId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.DestinationDataSource)
                  .WithMany()
                  .HasForeignKey(e => e.DestinationDataSourceId)
                  .OnDelete(DeleteBehavior.Restrict);
                  
            entity.HasMany(e => e.Executions)
                  .WithOne(e => e.MigrationJob)
                  .HasForeignKey(e => e.MigrationJobId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Indexes for performance
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.DestinationDataSourceId);
            entity.HasIndex(e => new { e.IsEnabled, e.ArchivedTime });
        });

        // MigrationExecution configuration
        modelBuilder.Entity<MigrationExecutionHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExecutedQuery).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            
            // Self-reference for retry tracking
            entity.HasOne(e => e.ParentExecution)
                  .WithMany()
                  .HasForeignKey(e => e.ParentExecutionId)
                  .OnDelete(DeleteBehavior.NoAction);

            // Indexes for performance
            entity.HasIndex(e => e.MigrationJobId);
            entity.HasIndex(e => new { e.Status, e.StartedAt });
            entity.HasIndex(e => e.StartedAt);
        });
    }

    protected static void ConfigureMetadataEntities(ModelBuilder modelBuilder)
    {
        // DatabaseMetadata configuration
        modelBuilder.Entity<DatabaseMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SchemaName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableDescription).HasMaxLength(1000);

            // Relationships
            entity.HasOne(e => e.DataSource)
                  .WithMany()
                  .HasForeignKey(e => e.DataSourceId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Columns)
                  .WithOne(e => e.DatabaseMetadata)
                  .HasForeignKey(e => e.DatabaseMetadataId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Indexes)
                  .WithOne(e => e.DatabaseMetadata)
                  .HasForeignKey(e => e.DatabaseMetadataId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Indexes for performance
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName }).IsUnique();
            entity.HasIndex(e => e.LastRefreshed);
        });

        // ColumnMetadata configuration
        modelBuilder.Entity<ColumnMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ColumnName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DataType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ForeignKeyTable).HasMaxLength(200);
            entity.Property(e => e.ForeignKeyColumn).HasMaxLength(200);
            entity.Property(e => e.DefaultValue).HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(1000);

            // Indexes for performance
            entity.HasIndex(e => e.DatabaseMetadataId);
            entity.HasIndex(e => new { e.DatabaseMetadataId, e.ColumnName });
        });

        // IndexMetadata configuration
        modelBuilder.Entity<IndexMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IndexName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Columns).IsRequired();

            // Indexes for performance
            entity.HasIndex(e => e.DatabaseMetadataId);
        });
    }

    protected static void ConfigureTaskEntity(ModelBuilder modelBuilder)
    {
        // QueryTask entity configuration
        modelBuilder.Entity<QueryTask>(entity =>
        {
            // Index for subscription lookup
            entity.HasIndex(t => t.SubscriptionId);

            // Composite index for filtering by resolution status
            entity.HasIndex(t => new { t.Resolved, t.CreatedTime });

            // Global ordering index
            entity.HasIndex(t => t.CreatedTime);

            // Field constraints
            entity.Property(t => t.ResolutionNotes)
                  .HasMaxLength(2000)
                  .IsUnicode(true);

            // One-to-many: Task has many Notifications
            entity.HasMany(t => t.Notifications)
                  .WithOne(n => n.Task)
                  .HasForeignKey(n => n.TaskId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.Subscription)
                  .WithMany()
                  .HasForeignKey(t => t.SubscriptionId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }

    protected static void ConfigureCommentEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Content)
                  .HasMaxLength(4000)
                  .IsRequired();

            entity.Property(e => e.UserId)
                  .HasMaxLength(100);

            entity.Property(e => e.UserName)
                  .HasMaxLength(200);

            // Composite index for efficient lookup by entity
            entity.HasIndex(e => new { e.EntityType, e.EntityId });

            // Index for ordering by creation time
            entity.HasIndex(e => e.CreatedTime);
        });
    }

    protected static void ConfigureAnomalyEntities(ModelBuilder modelBuilder)
    {
        // AnomalyConfig configuration
        modelBuilder.Entity<AnomalyConfig>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationships
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Unique index: one config per subscription
            entity.HasIndex(e => e.SubscriptionId)
                  .IsUnique();

            // Index for enabled configs
            entity.HasIndex(e => e.Enabled);
        });

        // AnomalyBaseline configuration
        modelBuilder.Entity<AnomalyBaseline>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationships
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Index for efficient time-based queries
            entity.HasIndex(e => new { e.SubscriptionId, e.ExecutionTime });

            // Index for lookback queries
            entity.HasIndex(e => e.ExecutionTime);
        });

        // AnomalyEvent configuration
        modelBuilder.Entity<AnomalyEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Severity)
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(e => e.Explanation)
                  .HasMaxLength(2000);

            entity.Property(e => e.AcknowledgedBy)
                  .HasMaxLength(200);

            // Relationships
            // NoAction prevents SQL Server cascade cycle error (Subscription -> Notification -> AnomalyEvent creates multiple paths)
            // Application code should explicitly delete AnomalyEvents when deleting Subscriptions if needed
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Notification)
                  .WithMany()
                  .HasForeignKey(e => e.NotificationId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Indexes for querying
            entity.HasIndex(e => new { e.SubscriptionId, e.DetectedTime });
            entity.HasIndex(e => new { e.Acknowledged, e.DetectedTime });
            entity.HasIndex(e => e.DetectedTime);
            entity.HasIndex(e => e.NotificationId);
        });
    }

    protected static void ConfigureAiEntities(ModelBuilder modelBuilder)
    {
        // AiUsageMetrics configuration
        modelBuilder.Entity<AiUsageMetrics>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Indexes for querying and analytics
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Provider);
            entity.HasIndex(e => e.OperationType);
            entity.HasIndex(e => e.DataSourceId);
        });

        // AiPromptTemplate configuration
        modelBuilder.Entity<AiPromptTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OperationType);
            entity.HasIndex(e => e.IsActive);
        });

        // AiAlertConfiguration configuration
        modelBuilder.Entity<AiAlertConfiguration>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Note: Don't add explicit HasIndex for DataSourceId/SubscriptionId - EF Core creates indexes automatically for FKs
            entity.HasIndex(e => e.Status);

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Subscription)
                .WithMany()
                .HasForeignKey(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AiConversationHistory configuration
        modelBuilder.Entity<AiConversationHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Note: Don't add explicit HasIndex for AiAlertConfigurationId - EF Core creates one automatically for the FK
            entity.HasIndex(e => e.TurnNumber);
            entity.HasIndex(e => e.Timestamp);

            entity.HasOne(e => e.AiAlertConfiguration)
                .WithMany(a => a.ConversationHistory)
                .HasForeignKey(e => e.AiAlertConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }

    protected static void ConfigureAiActorEntities(ModelBuilder modelBuilder)
    {
        // AiActor configuration
        modelBuilder.Entity<AiActor>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Instructions).IsRequired();
            entity.Property(e => e.CreatedByUserId).HasMaxLength(100);
            entity.Property(e => e.LastError).HasMaxLength(4000);

            // Indexes
            // Note: Don't add explicit HasIndex for DataSourceId - EF Core creates one automatically for the FK
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ArchivedTime);
            entity.HasIndex(e => new { e.DataSourceId, e.Status });
            entity.HasIndex(e => new { e.Status, e.ArchivedTime });

            // Relationships
            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Executions)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Conversations)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Queries)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Subscriptions)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AiActorExecution configuration
        modelBuilder.Entity<AiActorExecution>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.DecisionSummary).HasMaxLength(4000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            // AiActorId index is created by parent relationship (AiActor.Executions)
            entity.HasIndex(e => e.Phase);
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.AiActorId, e.StartedAt });

            // Relationships
            entity.HasOne(e => e.TriggeringSubscription)
                .WithMany()
                .HasForeignKey(e => e.TriggeringSubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);

            // NoAction prevents SQL Server cascade cycle (AiActor -> AiActorExecution and AiActor -> AiActorPlan -> AiActorExecution)
            entity.HasOne(e => e.AiActorPlan)
                .WithOne(p => p.AiActorExecution)
                .HasForeignKey<AiActorExecution>(e => e.AiActorPlanId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // AiActorConversation configuration
        modelBuilder.Entity<AiActorConversation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.MessageContent).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            // AiActorId index is created by parent relationship (AiActor.Conversations)
            entity.HasIndex(e => e.TurnNumber);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.AiActorId, e.TurnNumber });

            // Relationships
            // NoAction prevents SQL Server cascade cycle (AiActor -> AiActorConversation and AiActor -> AiActorExecution)
            entity.HasOne(e => e.AiActorExecution)
                .WithMany()
                .HasForeignKey(e => e.AiActorExecutionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // AiActorPlan configuration
        modelBuilder.Entity<AiActorPlan>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Analysis).IsRequired();
            entity.Property(e => e.ActionsJson).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.ReviewedByUserId).HasMaxLength(100);
            entity.Property(e => e.ReviewerComment).HasMaxLength(4000);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ProposedAt);
            entity.HasIndex(e => new { e.AiActorId, e.Status });
            entity.HasIndex(e => new { e.AiActorId, e.ProposedAt });

            // Relationships
            entity.HasOne(e => e.AiActor)
                .WithMany(a => a.Plans)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction prevents SQL Server cascade cycle error (AiActor -> AiActorPlan -> AiActorPlan creates multiple paths)
            // Application code should explicitly handle ParentPlanId when deleting plans if needed
            entity.HasOne(e => e.ParentPlan)
                .WithMany(p => p.Revisions)
                .HasForeignKey(e => e.ParentPlanId)
                .OnDelete(DeleteBehavior.NoAction);

            // NoAction prevents SQL Server cascade cycle (AiActor -> AiActorPlan -> QueryStepChangeHistory)
            entity.HasMany(e => e.ChangeHistory)
                .WithOne(c => c.AiActorPlan)
                .HasForeignKey(c => c.AiActorPlanId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // QueryStepChangeHistory configuration
        modelBuilder.Entity<QueryStepChangeHistory>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PreviousSql).IsRequired();
            entity.Property(e => e.NewSql).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.Property(e => e.ChangeReason).HasMaxLength(2000);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            entity.HasIndex(e => e.ChangedAt);
            entity.HasIndex(e => e.ChangeSource);
            entity.HasIndex(e => new { e.QueryStepId, e.ChangedAt });

            // Relationships
            entity.HasOne(e => e.QueryStep)
                .WithMany()
                .HasForeignKey(e => e.QueryStepId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction prevents SQL Server cascade cycles (multiple paths from AiActor hierarchy)
            entity.HasOne(e => e.AiActor)
                .WithMany()
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.NoAction);

            // NoAction prevents SQL Server cascade cycles (AiActor -> AiActorExecution and other paths)
            entity.HasOne(e => e.AiActorExecution)
                .WithMany()
                .HasForeignKey(e => e.AiActorExecutionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // Add indexes to Query for AiActorId and lock fields
        modelBuilder.Entity<Query>(entity =>
        {
            entity.HasIndex(e => e.AiActorId);
            entity.HasIndex(e => e.IsLocked);
            entity.Property(e => e.LockedByUserId).HasMaxLength(100);
        });

        // Add indexes to Subscription for AiActorId
        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasIndex(e => e.AiActorId);
        });
    }

    protected static void ConfigureQueryFolderEntities(ModelBuilder modelBuilder)
    {
        // QueryFolder configuration
        modelBuilder.Entity<QueryFolder>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Path).HasMaxLength(1000).IsRequired();

            // Self-referencing relationship for hierarchy
            entity.HasOne(e => e.ParentFolder)
                .WithMany(e => e.ChildFolders)
                .HasForeignKey(e => e.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict);

            // Relationship with queries
            entity.HasMany(e => e.Queries)
                .WithOne(q => q.Folder)
                .HasForeignKey(q => q.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for performance
            entity.HasIndex(e => e.ParentFolderId);
            entity.HasIndex(e => e.Path);
            entity.HasIndex(e => new { e.ParentFolderId, e.Name }).IsUnique();
            entity.HasIndex(e => new { e.ParentFolderId, e.SortOrder });
            entity.HasIndex(e => e.ArchivedTime);
        });

        // Add index to Query for FolderId
        modelBuilder.Entity<Query>(entity =>
        {
            entity.HasIndex(e => e.FolderId);
        });
    }

    protected static void ConfigureManualQueryExecutionLogEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManualQueryExecutionLog>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.Property(e => e.QueryText).IsRequired();
            entity.Property(e => e.ExecutionContext).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);

            // Relationship with DataSource
            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for querying
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => e.ExecutionContext);
            entity.HasIndex(e => new { e.UserId, e.CreatedTime });
            entity.HasIndex(e => new { e.DataSourceId, e.CreatedTime });
        });
    }

    protected static void ConfigureAppSettingEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(2000);
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasIndex(e => e.Category);
        });

        modelBuilder.Entity<AppSettingHistory>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SettingKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.OldValue).HasMaxLength(2000);
            entity.Property(e => e.NewValue).HasMaxLength(2000);
            entity.Property(e => e.ChangedByUserId).HasMaxLength(200);

            entity.HasIndex(e => e.SettingKey);
            entity.HasIndex(e => e.ChangedAt);
        });
    }

    protected static void ConfigureApprovalEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueryApprovalRequest>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.RequestedByUserId).HasMaxLength(100);
            entity.Property(e => e.RequestedByUserName).HasMaxLength(200);
            entity.Property(e => e.ReviewedByUserId).HasMaxLength(100);
            entity.Property(e => e.ReviewedByUserName).HasMaxLength(200);
            entity.Property(e => e.ReviewComment).HasMaxLength(2000);
            entity.Property(e => e.ChangeSummary).HasMaxLength(2000);

            // Indexes
            entity.HasIndex(e => new { e.QueryId, e.Status });
            entity.HasIndex(e => new { e.Status, e.CreatedTime });
            entity.HasIndex(e => e.Status);

            // Relationships
            entity.HasOne(e => e.Query)
                .WithMany()
                .HasForeignKey(e => e.QueryId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction to avoid cascade cycle through Query -> QueryVersion -> QueryApprovalRequest
            entity.HasOne(e => e.QueryVersion)
                .WithMany()
                .HasForeignKey(e => e.QueryVersionId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    protected static void ConfigureQueryVersionEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueryVersion>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Label).HasMaxLength(200);
            entity.Property(e => e.StepsJson).IsRequired();
            entity.Property(e => e.CreatedByUserId).HasMaxLength(100);
            entity.Property(e => e.ChangeSource).HasMaxLength(50);
            entity.Property(e => e.ChangeReason).HasMaxLength(2000);

            // Unique version number per query
            entity.HasIndex(e => new { e.QueryId, e.VersionNumber }).IsUnique();

            // Index for filtering by status
            entity.HasIndex(e => new { e.QueryId, e.Status });

            // Relationship with Query (cascade delete versions when query is deleted)
            entity.HasOne(e => e.Query)
                .WithMany(q => q.Versions)
                .HasForeignKey(e => e.QueryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Query.ActiveVersionId FK (self-referencing via QueryVersion)
        // SQL Server doesn't allow SetNull here due to the circular cascade path with QueryVersions->Queries.
        // ActiveVersionId must be nulled manually in application code before deleting a QueryVersion.
        modelBuilder.Entity<Query>(entity =>
        {
            entity.HasOne(q => q.ActiveVersion)
                .WithMany()
                .HasForeignKey(q => q.ActiveVersionId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    protected static void ConfigureUserManagementEntities(ModelBuilder modelBuilder)
    {
        // BeaconUser configuration
        modelBuilder.Entity<BeaconUser>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.IdentityProvider).HasMaxLength(500);
            entity.Property(e => e.UserName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.PasswordSalt).HasMaxLength(500);

            // Unique constraint on ExternalId
            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.HasIndex(e => new { e.IdentityProvider, e.ExternalId });

            // Unique constraint on UserName (only for non-archived users)
            entity.HasIndex(e => new { e.UserName, e.ArchivedTime });

            // Indexes for querying
            entity.HasIndex(e => e.Email);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.IsSuperAdmin);
            entity.HasIndex(e => e.IsInternalUser);
            entity.HasIndex(e => e.ArchivedTime);

            // Relationships
            entity.HasMany(e => e.UserRoles)
                .WithOne(ur => ur.User)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BeaconRole configuration
        modelBuilder.Entity<BeaconRole>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);

            // Unique constraint on role name
            entity.HasIndex(e => e.Name).IsUnique();

            // Relationships
            entity.HasMany(e => e.UserRoles)
                .WithOne(ur => ur.Role)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BeaconUserRole configuration
        modelBuilder.Entity<BeaconUserRole>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.AssignedByUserId).HasMaxLength(200);

            // Unique constraint on user-role combination
            entity.HasIndex(e => new { e.UserId, e.RoleId }).IsUnique();

            // Indexes for querying
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.RoleId);
            entity.HasIndex(e => e.AssignedAt);
        });
    }

    protected static void ConfigureDashboardEntities(ModelBuilder modelBuilder)
    {
        // Dashboard configuration
        modelBuilder.Entity<Dashboard>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CreatedByUserId).HasMaxLength(100);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(200);
            entity.Property(e => e.LayoutConfiguration);

            entity.HasIndex(e => e.CreatedByUserId);
            entity.HasIndex(e => e.IsShared);
            entity.HasIndex(e => e.IsDefault);
            entity.HasIndex(e => e.ArchivedTime);

            entity.HasMany(e => e.Widgets)
                .WithOne(w => w.Dashboard)
                .HasForeignKey(w => w.DashboardId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Permissions)
                .WithOne(p => p.Dashboard)
                .HasForeignKey(p => p.DashboardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DashboardWidget configuration
        modelBuilder.Entity<DashboardWidget>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ConfigurationJson).IsRequired();

            entity.HasIndex(e => e.DashboardId);
            entity.HasIndex(e => e.WidgetType);
            entity.HasIndex(e => new { e.DashboardId, e.SortOrder });
        });

        // DashboardPermission configuration
        modelBuilder.Entity<DashboardPermission>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.GrantedByUserId).HasMaxLength(100);

            entity.HasIndex(e => e.DashboardId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.DashboardId, e.UserId }).IsUnique();
        });
    }

    protected static void ConfigureDataQualityEntities(ModelBuilder modelBuilder)
    {
        // DataContract configuration
        modelBuilder.Entity<DataContract>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CronExpression).HasMaxLength(100).IsRequired();
            entity.Property(e => e.OwnerUserId).HasMaxLength(100);

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Rules)
                .WithOne(r => r.DataContract)
                .HasForeignKey(r => r.DataContractId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Evaluations)
                .WithOne(e => e.DataContract)
                .HasForeignKey(e => e.DataContractId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.ArchivedTime);
            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName });
        });

        // DataContractRule configuration
        modelBuilder.Entity<DataContractRule>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ColumnName).HasMaxLength(200);
            entity.Property(e => e.Configuration).IsRequired();

            entity.HasIndex(e => e.DataContractId);
        });

        // DataQualityEvaluation configuration
        modelBuilder.Entity<DataQualityEvaluation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasMany(e => e.RuleResults)
                .WithOne(r => r.DataQualityEvaluation)
                .HasForeignKey(r => r.DataQualityEvaluationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.DataContractId);
            entity.HasIndex(e => e.CreatedTime);
        });

        // DataQualityRuleResult configuration
        modelBuilder.Entity<DataQualityRuleResult>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ActualValue).HasMaxLength(500);
            entity.Property(e => e.ExpectedValue).HasMaxLength(500);
            entity.Property(e => e.Message).HasMaxLength(2000);

            // NoAction to avoid multi-path cascade (DataContract -> Rule -> RuleResult and DataContract -> Evaluation -> RuleResult)
            entity.HasOne(e => e.DataContractRule)
                .WithMany()
                .HasForeignKey(e => e.DataContractRuleId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(e => e.DataQualityEvaluationId);
            entity.HasIndex(e => e.DataContractRuleId);
        });

        // DataQualityScore configuration
        modelBuilder.Entity<DataQualityScore>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName }).IsUnique();
            entity.HasIndex(e => e.EvaluatedAt);
        });
    }

    protected static void ConfigureProjectEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);

            entity.HasMany(e => e.DataSources)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Repositories)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Documentations)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Name);
        });

        modelBuilder.Entity<ProjectDataSource>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.ProjectId, e.DataSourceId }).IsUnique();
        });

        modelBuilder.Entity<GitHubRepository>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RepositoryUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Branch).HasMaxLength(200);
            entity.Property(e => e.ScanCronExpression).HasMaxLength(100);
            entity.Property(e => e.LastScanError).HasMaxLength(4000);

            entity.HasMany(e => e.CodeReferences)
                .WithOne(e => e.GitHubRepository)
                .HasForeignKey(e => e.GitHubRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.ScanStatus);
        });

        modelBuilder.Entity<CodeReference>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.SchemaName).HasMaxLength(200);
            entity.Property(e => e.TableName).HasMaxLength(200);
            entity.Property(e => e.ColumnName).HasMaxLength(200);
            entity.Property(e => e.CodeSnippet).HasMaxLength(4000);
            entity.Property(e => e.ClassName).HasMaxLength(200);
            entity.Property(e => e.MethodName).HasMaxLength(200);

            entity.HasIndex(e => e.GitHubRepositoryId);
            entity.HasIndex(e => new { e.SchemaName, e.TableName });
            entity.HasIndex(e => e.ReferenceType);
        });

        modelBuilder.Entity<ProjectDocumentation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.GeneratedAt);
        });

        modelBuilder.Entity<ProjectDocumentationSection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SectionType);

            entity.HasOne(e => e.Documentation)
                .WithMany(d => d.Sections)
                .HasForeignKey(e => e.ProjectDocumentationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }

    protected static void ConfigureApiKeyEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKeyCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.KeyHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.KeyPrefix).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Scopes).HasMaxLength(500);
            entity.Property(e => e.AllowedProjectIds).HasMaxLength(500);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.KeyPrefix);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.IsRevoked);
        });
    }

    protected static void ConfigureMcpEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();

            entity.HasOne(e => e.ApiKey)
                .WithMany()
                .HasForeignKey(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.HasIndex(e => e.LastActivityAt);
        });

        modelBuilder.Entity<McpAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tool).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Parameters).HasMaxLength(4000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);

            entity.HasOne(e => e.Session)
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => e.Tool);
        });

        modelBuilder.Entity<McpSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AskSystemPrompt).HasMaxLength(4000);
            entity.Property(e => e.GlobalInstruction).HasMaxLength(4000);
            entity.Property(e => e.GetContextDescription).HasMaxLength(1000);
            entity.Property(e => e.SearchDescription).HasMaxLength(1000);
            entity.Property(e => e.QueryDescription).HasMaxLength(1000);
            entity.Property(e => e.GetDocumentationDescription).HasMaxLength(1000);
            entity.Property(e => e.AskDescription).HasMaxLength(1000);
            entity.Property(e => e.CustomPiiPatterns).HasMaxLength(4000);
        });
    }

    protected static void ConfigureMcpLearningEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpQuerySignal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tool).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Question).HasMaxLength(4000).IsRequired();
            entity.Property(e => e.IntentClassification).HasMaxLength(50);
            entity.Property(e => e.SchemaValidationError).HasMaxLength(4000);
            entity.Property(e => e.ExecutionError).HasMaxLength(4000);

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => e.IsSuccessful);
        });

        modelBuilder.Entity<McpLearnedPattern>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ColumnName).HasMaxLength(200);
            entity.Property(e => e.PatternContent).IsRequired();
            entity.Property(e => e.ExampleQuestion).HasMaxLength(4000);

            entity.HasIndex(e => new { e.DataSourceId, e.Status, e.TableName });
            entity.HasIndex(e => e.ProjectId);
        });

        modelBuilder.Entity<McpDocumentationPatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TargetIdentifier).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ProposedContent).IsRequired();
            entity.Property(e => e.Reasoning).HasMaxLength(2000).IsRequired();

            entity.HasIndex(e => new { e.ProjectId, e.Status });
            entity.HasIndex(e => e.DataSourceId);
        });
    }
}
