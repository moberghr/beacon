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

    public DbSet<QueryParameter> QueryParameters => Set<QueryParameter>();

    public DbSet<QueryStep> QuerySteps => Set<QueryStep>();

    public DbSet<QueryStepParameter> QueryStepParameters => Set<QueryStepParameter>();

    public DbSet<DataSource> DataSources => Set<DataSource>();

    public DbSet<QueryExecutionHistory> QueryExecutionHistory => Set<QueryExecutionHistory>();

    public DbSet<Recipient> Recipients => Set<Recipient>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<AlertingTask> Tasks => Set<AlertingTask>();

    public DbSet<MigrationJob> MigrationJobs => Set<MigrationJob>();

    public DbSet<MigrationExecutionHistory> MigrationExecutions => Set<MigrationExecutionHistory>();

    public DbSet<DatabaseMetadata> DatabaseMetadata => Set<DatabaseMetadata>();

    public DbSet<ColumnMetadata> ColumnMetadata => Set<ColumnMetadata>();

    public DbSet<IndexMetadata> IndexMetadata => Set<IndexMetadata>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Derived classes can use DefaultSchema
        SetSoftDeleteQueryFilter(modelBuilder);
        ConfigureMigrationEntities(modelBuilder);
        ConfigureMetadataEntities(modelBuilder);
        ConfigureTaskEntity(modelBuilder);
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
        // AlertingTask entity configuration
        modelBuilder.Entity<AlertingTask>(entity =>
        {
            entity.ToTable("Tasks");

            // Unique index for subscription (one task per subscription)
            entity.HasIndex(t => t.SubscriptionId)
                  .IsUnique()
                  .HasDatabaseName("IX_Task_SubscriptionId_Unique");

            // Composite index for filtering by resolution status
            entity.HasIndex(t => new { t.Resolved, t.CreatedTime })
                  .HasDatabaseName("IX_Task_Resolved_CreatedTime");

            // Global ordering index
            entity.HasIndex(t => t.CreatedTime)
                  .HasDatabaseName("IX_Task_CreatedTime");

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
}
