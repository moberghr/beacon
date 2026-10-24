using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Entities.RowSecurity;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
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

    public DbSet<TaskWatcher> TaskWatchers => Set<TaskWatcher>();

    public DbSet<MigrationJob> MigrationJobs => Set<MigrationJob>();

    public DbSet<MigrationExecutionHistory> MigrationExecutions => Set<MigrationExecutionHistory>();

    public DbSet<DatabaseMetadata> DatabaseMetadata => Set<DatabaseMetadata>();

    public DbSet<ColumnMetadata> ColumnMetadata => Set<ColumnMetadata>();

    public DbSet<IndexMetadata> IndexMetadata => Set<IndexMetadata>();

    public DbSet<SchemaRelationship> SchemaRelationships => Set<SchemaRelationship>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<AnomalyConfig> AnomalyConfigs => Set<AnomalyConfig>();

    public DbSet<AnomalyBaseline> AnomalyBaselines => Set<AnomalyBaseline>();

    public DbSet<AnomalyEvent> AnomalyEvents => Set<AnomalyEvent>();

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

    public DbSet<ChangeApprovalRequest> ChangeApprovalRequests => Set<ChangeApprovalRequest>();

    // App Settings
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<AppSettingHistory> AppSettingHistory => Set<AppSettingHistory>();

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

    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();

    public DbSet<GitHubRepository> GitHubRepositories => Set<GitHubRepository>();

    public DbSet<CodeReference> CodeReferences => Set<CodeReference>();

    public DbSet<ProjectDocumentation> ProjectDocumentations => Set<ProjectDocumentation>();

    public DbSet<ProjectDocumentationSection> ProjectDocumentationSections => Set<ProjectDocumentationSection>();

    public DbSet<ProjectImportedDocument> ProjectImportedDocuments => Set<ProjectImportedDocument>();

    // API Keys & MCP
    public DbSet<ApiKeyCredential> ApiKeyCredentials => Set<ApiKeyCredential>();

    public DbSet<McpAuditLog> McpAuditLogs => Set<McpAuditLog>();

    public DbSet<McpAuditStatement> McpAuditStatements => Set<McpAuditStatement>();

    public DbSet<McpSettings> McpSettings => Set<McpSettings>();
    public DbSet<McpProjectSettings> McpProjectSettings => Set<McpProjectSettings>();

    // MCP Learning
    public DbSet<McpQuerySignal> McpQuerySignals => Set<McpQuerySignal>();
    public DbSet<McpLearnedPattern> McpLearnedPatterns => Set<McpLearnedPattern>();
    public DbSet<McpDocumentationPatch> McpDocumentationPatches => Set<McpDocumentationPatch>();
    public DbSet<McpEmbedding> McpEmbeddings => Set<McpEmbedding>();
    public DbSet<McpDocChunk> McpDocChunks => Set<McpDocChunk>();
    public DbSet<McpGlossaryTerm> McpGlossaryTerms => Set<McpGlossaryTerm>();

    // MCP Eval harness
    public DbSet<McpEvalCase> McpEvalCases => Set<McpEvalCase>();
    public DbSet<McpEvalRun> McpEvalRuns => Set<McpEvalRun>();
    public DbSet<McpEvalResult> McpEvalResults => Set<McpEvalResult>();

    // Security audit trail (append-only)
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();

    // Row security
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<UserAttributeValue> UserAttributeValues => Set<UserAttributeValue>();
    public DbSet<RowPolicy> RowPolicies => Set<RowPolicy>();
    public DbSet<RowPolicyVersion> RowPolicyVersions => Set<RowPolicyVersion>();
    public DbSet<TableClassification> TableClassifications => Set<TableClassification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var resolvedSchema = BeaconSchema.Resolve(this) ?? DefaultSchema;

        if (!string.IsNullOrEmpty(resolvedSchema))
        {
            modelBuilder.HasDefaultSchema(resolvedSchema);
        }

        SetSoftDeleteQueryFilter(modelBuilder);
        ConfigureMigrationEntities(modelBuilder);
        ConfigureMetadataEntities(modelBuilder);
        ConfigureTaskEntity(modelBuilder);
        ConfigureExecutionHistoryEntity(modelBuilder);
        ConfigureTaskWatcherEntities(modelBuilder);
        ConfigureCommentEntity(modelBuilder);
        ConfigureAnomalyEntities(modelBuilder);
        ConfigureAiActorEntities(modelBuilder);
        ConfigureQueryFolderEntities(modelBuilder);
        ConfigureManualQueryExecutionLogEntity(modelBuilder);
        ConfigureUserManagementEntities(modelBuilder);
        ConfigureAppSettingEntities(modelBuilder);
        ConfigureQueryVersionEntities(modelBuilder);
        ConfigureApprovalEntities(modelBuilder);
        ConfigurePendingLlmConnectionIndex(modelBuilder);
        ConfigureQueryMcpToolEntities(modelBuilder);
        ConfigureDataQualityEntities(modelBuilder);
        ConfigureProjectEntities(modelBuilder);
        ConfigureApiKeyEntities(modelBuilder);
        ConfigureMcpEntities(modelBuilder);
        ConfigureMcpLearningEntities(modelBuilder);
        ConfigureMcpEmbeddingEntities(modelBuilder);
        ConfigureMcpDocChunkEntities(modelBuilder);
        ConfigureMcpGlossaryEntities(modelBuilder);
        ConfigureMcpEvalEntities(modelBuilder);
        ConfigureSecurityAuditEntities(modelBuilder);
        ConfigureRowSecurityEntities(modelBuilder);
        ConfigureAttributionColumns(modelBuilder);
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
}
