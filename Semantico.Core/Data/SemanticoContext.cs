using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Entities.DataMigration;
using Semantico.Core.Data.Entities.Metadata;
using System.Linq.Expressions;

namespace Semantico.Core.Data;

public abstract partial class SemanticoContext : DbContext
{
    private readonly string _defaultSchema;
    protected SemanticoContext(DbContextOptions options, string defaultSchema = "semantico")
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

    public DbSet<DataSourceDocumentation> DataSourceDocumentations => Set<DataSourceDocumentation>();

    public DbSet<DocumentationSection> DocumentationSections => Set<DocumentationSection>();

    public DbSet<DocumentationVersion> DocumentationVersions => Set<DocumentationVersion>();

    public DbSet<AiPromptTemplate> AiPromptTemplates => Set<AiPromptTemplate>();

    public DbSet<AiAlertConfiguration> AiAlertConfigurations => Set<AiAlertConfiguration>();

    public DbSet<AiConversationHistory> AiConversationHistories => Set<AiConversationHistory>();

    public DbSet<DocumentationAgentRun> DocumentationAgentRuns => Set<DocumentationAgentRun>();

    public DbSet<AiActor> AiActors => Set<AiActor>();

    public DbSet<AiActorExecution> AiActorExecutions => Set<AiActorExecution>();

    public DbSet<AiActorConversation> AiActorConversations => Set<AiActorConversation>();

    public DbSet<AiActorPlan> AiActorPlans => Set<AiActorPlan>();

    public DbSet<QueryStepChangeHistory> QueryStepChangeHistory => Set<QueryStepChangeHistory>();

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
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.Cascade);

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

        // DataSourceDocumentation configuration
        modelBuilder.Entity<DataSourceDocumentation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.GeneratedAt);

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DocumentationSection configuration
        modelBuilder.Entity<DocumentationSection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DocumentationId);
            entity.HasIndex(e => e.TableName);
            entity.HasIndex(e => e.SectionType);

            entity.HasOne(e => e.Documentation)
                .WithMany(d => d.Sections)
                .HasForeignKey(e => e.DocumentationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DocumentationVersion configuration
        modelBuilder.Entity<DocumentationVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DocumentationId);
            entity.HasIndex(e => e.CreatedTime);

            entity.HasOne(e => e.Documentation)
                .WithMany(d => d.Versions)
                .HasForeignKey(e => e.DocumentationId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SubscriptionId);

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
            entity.HasIndex(e => e.AiAlertConfigurationId);
            entity.HasIndex(e => e.TurnNumber);
            entity.HasIndex(e => e.Timestamp);

            entity.HasOne(e => e.AiAlertConfiguration)
                .WithMany(a => a.ConversationHistory)
                .HasForeignKey(e => e.AiAlertConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DocumentationAgentRun configuration
        modelBuilder.Entity<DocumentationAgentRun>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ProgressMessage).HasMaxLength(500);
            entity.Property(e => e.LastError).HasMaxLength(4000);

            // Indexes for querying
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.DocumentationId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CurrentPhase);
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.DataSourceId, e.Status });

            // Relationships
            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Documentation)
                .WithMany()
                .HasForeignKey(e => e.DocumentationId)
                .OnDelete(DeleteBehavior.SetNull);
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
            entity.HasIndex(e => e.DataSourceId);
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
            entity.HasIndex(e => e.AiActorId);
            entity.HasIndex(e => e.TriggeringSubscriptionId);
            entity.HasIndex(e => e.Phase);
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.AiActorId, e.StartedAt });
            entity.HasIndex(e => e.AiActorPlanId);

            // Relationships
            entity.HasOne(e => e.TriggeringSubscription)
                .WithMany()
                .HasForeignKey(e => e.TriggeringSubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.AiActorPlan)
                .WithOne(p => p.AiActorExecution)
                .HasForeignKey<AiActorExecution>(e => e.AiActorPlanId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AiActorConversation configuration
        modelBuilder.Entity<AiActorConversation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.MessageContent).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);

            // Indexes
            entity.HasIndex(e => e.AiActorId);
            entity.HasIndex(e => e.AiActorExecutionId);
            entity.HasIndex(e => e.TurnNumber);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.AiActorId, e.TurnNumber });

            // Relationships
            entity.HasOne(e => e.AiActorExecution)
                .WithMany()
                .HasForeignKey(e => e.AiActorExecutionId)
                .OnDelete(DeleteBehavior.SetNull);
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
            entity.HasIndex(e => e.AiActorId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ProposedAt);
            entity.HasIndex(e => e.ParentPlanId);
            entity.HasIndex(e => new { e.AiActorId, e.Status });
            entity.HasIndex(e => new { e.AiActorId, e.ProposedAt });

            // Relationships
            entity.HasOne(e => e.AiActor)
                .WithMany(a => a.Plans)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ParentPlan)
                .WithMany(p => p.Revisions)
                .HasForeignKey(e => e.ParentPlanId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.ChangeHistory)
                .WithOne(c => c.AiActorPlan)
                .HasForeignKey(c => c.AiActorPlanId)
                .OnDelete(DeleteBehavior.SetNull);
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
            entity.HasIndex(e => e.QueryStepId);
            entity.HasIndex(e => e.AiActorId);
            entity.HasIndex(e => e.AiActorExecutionId);
            entity.HasIndex(e => e.AiActorPlanId);
            entity.HasIndex(e => e.ChangedAt);
            entity.HasIndex(e => e.ChangeSource);
            entity.HasIndex(e => new { e.QueryStepId, e.ChangedAt });

            // Relationships
            entity.HasOne(e => e.QueryStep)
                .WithMany()
                .HasForeignKey(e => e.QueryStepId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.AiActor)
                .WithMany()
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.AiActorExecution)
                .WithMany()
                .HasForeignKey(e => e.AiActorExecutionId)
                .OnDelete(DeleteBehavior.SetNull);
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
}
