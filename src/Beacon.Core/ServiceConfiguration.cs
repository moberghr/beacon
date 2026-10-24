using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Adapters.Mail;
using Beacon.Core.Adapters.Slack;
using Beacon.Core.Adapters.Teams;
using Beacon.Core.Http;
using Beacon.Core.Adapters.Webhook;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Data;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Core.Services.Retention;
using Beacon.Core.Services.Shared;
using Beacon.Core.Services.Validation;
using Microsoft.Extensions.Options;
using Beacon.Core.Worker;
using Beacon.Core.Worker.Services;
using Beacon.Core.SavedQueries;

namespace Beacon.Core;

public static class ServiceConfiguration
{
    /// <summary>
    /// The placeholder encryption key the sample host once shipped in its appsettings.json. It is publicly known, so
    /// <see cref="AddBeaconServices"/> refuses it as <c>Beacon:EncryptionKey</c> in every environment; it can only be listed
    /// in <c>Beacon:PreviousEncryptionKeys</c>, to keep reading values written with it.
    /// </summary>
    public const string DefaultEncryptionKey = "DefaultKey_ChangeInProduction_MustBe32CharsLong!";

    /// <summary>The configuration key of the keys values written earlier may be encrypted with (decrypt only).</summary>
    public const string PreviousEncryptionKeysKey = "Beacon:PreviousEncryptionKeys";

    /// <summary>
    /// The configuration key that keeps stored values in the legacy v1 format (unauthenticated AES-CBC) readable. On by
    /// default; turn it off once <see cref="IStoredValueReEncryptionService"/> reports no legacy value left
    /// (<see cref="StoredValueReEncryptionResult.LegacyFormatReadRemovable"/>).
    /// </summary>
    public const string AllowLegacyCbcKey = "Beacon:Encryption:AllowLegacyCbc";

    /// <summary>
    /// <c>Beacon:PreviousEncryptionKeys</c> lists at least one key: stored values may still be encrypted with it, and
    /// <see cref="IStoredValueReEncryptionService"/> moves them to the current key.
    /// </summary>
    public static bool HasPreviousEncryptionKeys(IConfiguration configuration)
    {
        return PreviousEncryptionKeys(configuration).Any(x => !string.IsNullOrWhiteSpace(x));
    }

    /// <summary>
    /// <c>Beacon:PreviousEncryptionKeys</c>: its list entries, or, when it is a single value instead of a list, that value.
    /// </summary>
    internal static string[] PreviousEncryptionKeys(IConfiguration configuration)
    {
        var section = configuration.GetSection(PreviousEncryptionKeysKey);
        var listed = section.Get<string[]>() ?? [];

        return listed.Length == 0 && !string.IsNullOrWhiteSpace(section.Value)
            ? [section.Value]
            : listed;
    }

    /// <summary>
    /// Adds Beacon's core services and starts the host chain: continue with the connectors (<c>.AddPostgreSqlConnector()</c>,
    /// ...), <c>.AddAI()</c>, <c>.AddMcp()</c>, <c>.AddApi()</c> and <c>.AddCookieAuth&lt;TClaims&gt;()</c>, and end it with
    /// the database provider (<c>.UsePostgreSql()</c> or <c>.UseSqlServer()</c>). AI is switched on by <c>.AddAI()</c>,
    /// never by <see cref="BeaconConfiguration.UseAI"/> here: setting it in <paramref name="configure"/> throws, so a host
    /// cannot turn AI on without registering it.
    /// </summary>
    public static BeaconBuilder AddBeacon(this IServiceCollection services, IConfiguration configuration, Action<BeaconConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = services.AddBeaconCore(configuration, x =>
        {
            configure(x);
            if (x.UseAI)
            {
                throw new InvalidOperationException(
                    "AddBeacon does not take UseAI = true: call .AddAI() on the builder it returns, which registers Beacon's AI services and switches AI on.");
            }
        });
        BeaconBuilder.MarkChain(services);

        return builder;
    }

    /// <summary>
    /// Adds core Beacon services (without UI components).
    /// Chain with .UsePostgreSql() or .UseSqlServer() to configure the database provider.
    /// </summary>
    /// <remarks>
    /// The fluent chain is the preferred way to wire Beacon: <see cref="AddBeacon"/> and, in place of <c>UseAI = true</c>,
    /// <c>.AddAI()</c> on the builder it returns. This method stays supported and becomes obsolete in a later release.
    /// </remarks>
    public static BeaconBuilder AddBeaconServices(this IServiceCollection services, IConfiguration configuration, Action<BeaconConfiguration> beaconConfiguration)
    {
        return services.AddBeaconCore(configuration, beaconConfiguration);
    }

    internal static BeaconBuilder AddBeaconCore(this IServiceCollection services, IConfiguration configuration, Action<BeaconConfiguration> beaconConfiguration)
    {
        var configurationOptions = new BeaconConfiguration();
        beaconConfiguration(configurationOptions);
        configurationOptions.ValidateCore();

        // Fail fast if JWT authentication is enabled without a way to verify token signatures.
        configurationOptions.Authentication.Jwt?.Validate();

        // Register configuration for access by adapters and other services
        services.AddSingleton(configurationOptions);

        services.AddHttpClient();
        services.AddBeaconOutboundHttpClients();
        services.AddMemoryCache();

        // Note: IBeaconUserContext is registered by the UI layer; Core provides the interfaces and implementations.

        // Auth providers are TryAdd registrations, so the first one wins: an explicitly configured provider type
        // here, then the database-backed providers when user management is on (below), and only then the
        // Default* fallbacks (registered last, after the user-management block).
        if (configurationOptions.Authorization.ProviderType != null)
        {
            services.TryAddScoped(
                typeof(IBeaconAuthorizationProvider),
                configurationOptions.Authorization.ProviderType);
        }

        if (configurationOptions.Authentication.ProviderType != null)
        {
            services.TryAddScoped(
                typeof(IBeaconAuthenticationProvider),
                configurationOptions.Authentication.ProviderType);
        }

        // MediatR for CQRS pattern
        // Project authorization runs before the handler of every request that declares ProjectChecks; the behaviour
        // is registered once and applies to every handler assembly.
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ServiceConfiguration).Assembly);
            cfg.AddOpenBehavior(typeof(Authorization.ProjectAccess.ProjectAuthorizationBehavior<,>));
        });

        // Encryption service for sensitive data (e.g., connection strings). The key must be strong in every environment
        // (base64 of at least 32 bytes, never a published or placeholder value); the message never contains it. Values
        // written with a key that was replaced stay readable through Beacon:PreviousEncryptionKeys (decrypt only), and values in
        // the legacy v1 format while Beacon:Encryption:AllowLegacyCbc is on (the default).
        var encryptionKey = configuration["Beacon:EncryptionKey"] ?? string.Empty;
        var encryptionKeyProblem = EncryptionKeyRequirements.Problem(encryptionKey);
        if (encryptionKeyProblem != null)
        {
            throw new InvalidOperationException(encryptionKeyProblem);
        }

        // A single value (Beacon__PreviousEncryptionKeys rather than Beacon__PreviousEncryptionKeys__0) binds to no list
        // entry; it is read as the one previous key, and a startup warning names the list form.
        var previousEncryptionKeys = PreviousEncryptionKeys(configuration);
        services.AddSingleton<IEncryptionService>(x => new EncryptionService(
            encryptionKey,
            previousEncryptionKeys,
            x.GetService<Microsoft.Extensions.Logging.ILogger<EncryptionService>>(),
            configuration.GetValue(AllowLegacyCbcKey, true)));
        services.TryAddTransient<IStoredValueReEncryptionService, StoredValueReEncryptionService>();

        // Keyed hash for MCP caller audit identities: from Beacon:Mcp:CallerHashKey when set (so the hashes outlive a
        // change of the encryption key), else from the encryption key, each through its own purpose-bound derivation.
        services.TryAddSingleton(new Mcp.McpCallerSubjectHasher(Mcp.McpCallerSubjectHasher.KeySource(configuration, encryptionKey)));

        // The MCP audit's keyed content hashes (what a call received, each statement it ran), from the same secret through
        // their own derivation; and the scoped log of the statements a call ran on data sources, which its audit row takes.
        services.TryAddSingleton(new Mcp.McpContentHasher(Mcp.McpCallerSubjectHasher.KeySource(configuration, encryptionKey)));
        services.TryAddScoped<Mcp.IMcpStatementLog, Mcp.McpStatementLog>();
        services.TryAddScoped<Helpers.LegacyListTruncation>();

        // The security audit trail (Beacon:Audit): append-only events with a keyed HMAC each (the integrity key, else one
        // derived for this purpose from the caller-hash secret; earlier keys still verify), sealed and kept append-only by
        // the interceptor both providers register, written by the scoped recorder, and kept forever unless a retention of
        // at least a year is set. ValidateOnStart so a retention or key out of range fails the host at boot.
        services.AddOptions<Configuration.SecurityAuditOptions>()
            .Bind(configuration.GetSection(Configuration.SecurityAuditOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.SecurityAuditOptions>, Configuration.SecurityAuditOptionsValidator>());
        services.TryAddSingleton(SecurityAudit.SecurityAuditIntegrity.From(configuration, encryptionKey, previousEncryptionKeys));
        services.TryAddSingleton<SecurityAuditInterceptor>();
        services.TryAddScoped<SecurityAudit.ISecurityAuditRecorder, SecurityAudit.SecurityAuditRecorder>();
        services.TryAddTransient<SecurityAudit.ISecurityAuditRetentionService, SecurityAudit.SecurityAuditRetentionService>();

        // Reads of audit data are recorded before they return anything (fail closed), an identical read by the same actor
        // within a minute once; one verification of the trail runs at a time per process, the scheduled one included.
        services.TryAddSingleton<SecurityAudit.SecurityAuditReadGate>();
        services.TryAddScoped<SecurityAudit.SecurityAuditReads>();
        services.TryAddTransient<SecurityAudit.ISecurityAuditVerificationService, SecurityAudit.SecurityAuditVerificationService>();

        // Keyed hashes of where governed recipients deliver: under a key derived from the encryption key for this purpose;
        // hashes written under a previous key keep matching until the re-encryption run rewrites them.
        services.TryAddSingleton(new ChangeApprovals.RecipientDestinationHasher(encryptionKey, previousEncryptionKeys));

        // Keyed hashes of governed data-source connection definitions and of the LLM connection, under keys derived from the
        // encryption key for each purpose; hashes written under a previous key keep verifying.
        services.TryAddSingleton(new ChangeApprovals.ConnectionDefinitionHasher(encryptionKey, previousEncryptionKeys));

        // Notification channels (Beacon:Notifications): where recipients may point, the one outbound HTTP policy the
        // adapters use, and encryption of recipient secrets at rest. ValidateOnStart so a malformed allow-list entry
        // fails the host at boot.
        services.AddOptions<Configuration.NotificationChannelOptions>()
            .Bind(configuration.GetSection(Configuration.NotificationChannelOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.NotificationChannelOptions>, Configuration.NotificationChannelOptionsValidator>());
        services.TryAddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
        services.TryAddSingleton<OutboundAddressPolicy>();
        services.TryAddSingleton<NotificationDestinationPolicy>();
        services.TryAddSingleton<RecipientSecretProtector>();
        services.TryAddSingleton<RecipientSecretEditor>();
        services.TryAddSingleton<NotificationHttpSender>();
        services.TryAddTransient<IRecipientSecretEncryptionService, RecipientSecretEncryptionService>();
        services.AddNotificationHttpClient();

        services.AddSingleton<IAdapter, TeamsAdapter>();
        services.AddSingleton<IAdapter, SlackAdapter>();
        if (configurationOptions.EmailAdapter != null)
        {
            services.TryAddSingleton(typeof(IEmailAdapter), configurationOptions.EmailAdapter);
            services.AddSingleton<IAdapter, EmailAdapter>();
        }
        services.TryAddSingleton<IJiraRestClientFactory, JiraRestClientFactory>();
        services.TryAddSingleton<IJiraApiAdapter, JiraApiAdapter>();
        services.AddSingleton<IAdapter, JiraAdapter>();
        services.AddSingleton<IAdapter, WebhookAdapter>();
        services.TryAddSingleton<AdapterFactory>();

        // Data source providers — DatabaseProvider is always registered (connector projects register connection factories and engine types)
        // Non-database providers (CloudWatch, Databricks, BigQuery) are registered by their connector projects
        services.AddTransient<Services.Providers.IDataSourceProvider, Services.Providers.DatabaseProvider>();
        services.TryAddTransient<Services.Providers.IDataSourceProviderFactory, Services.Providers.DataSourceProviderFactory>();

        // The one way Beacon's own features run a statement on a data source (the execution gate, then the read-only
        // provider path), and the check a query step's SQL passes before it is stored.
        services.TryAddTransient<GatedSqlRunner>();
        // MCP MaxExplainCost: the planner's estimate a statement on a PostgreSQL source is held to before it runs.
        services.TryAddTransient<IQueryCostLimit, QueryCostLimit>();
        services.TryAddTransient<IStepSqlSaveCheck, StepSqlSaveCheck>();
        // The check SQL passes before Beacon stores it as grounding (feedback, golden cases, learned patterns).
        services.TryAddTransient<IGroundingSqlCheck, GroundingSqlCheck>();

        // Domain services
        services.TryAddTransient<IJobService, JobService>();
        services.TryAddTransient<INotificationService, NotificationService>();
        services.TryAddTransient<IDataSourceService, DataSourceService>();
        services.TryAddTransient<IHeldDataSourceProbe, DataSourceService>();
        services.TryAddTransient<IQueryService, QueryService>();
        services.TryAddTransient<IStoredQueryPreview, QueryService>();
        services.TryAddTransient<IQueryProposalCheck, QueryService>();
        services.TryAddTransient<IQueryExecutionPreviewService, QueryExecutionPreviewService>();
        services.TryAddTransient<ISubscriptionService, SubscriptionService>();
        services.TryAddTransient<IRecipientService, RecipientService>();
        services.TryAddTransient<ITaskService, TaskService>();
        services.TryAddTransient<IControlTowerService, ControlTowerService>();
        services.TryAddTransient<IMigrationService, MigrationService>();
        services.TryAddSingleton<DataMigration.MigrationBulkWriters>();
        services.TryAddTransient<IDatabaseMetadataService, DatabaseMetadataService>();
        services.TryAddTransient<Services.Metadata.IColumnValueSampler, Services.Metadata.ColumnValueSampler>();
        services.TryAddTransient<Services.Metadata.ISchemaRelationshipSyncService, Services.Metadata.SchemaRelationshipSyncService>();
        services.TryAddTransient<Services.Metadata.ISchemaGraphService, Services.Metadata.SchemaGraphService>();
        // The stored catalog the ask path reads, cached per data source and project; every catalog write invalidates it.
        services.TryAddSingleton<Services.Metadata.CatalogSnapshotCache>();
        services.TryAddSingleton<Services.Metadata.ICatalogSnapshotCache>(x => x.GetRequiredService<Services.Metadata.CatalogSnapshotCache>());
        services.TryAddTransient<IAnomalyDetectionService, AnomalyDetectionService>();
        services.TryAddTransient<IDataQualitySqlGenerator, DataQualitySqlGenerator>();
        services.TryAddTransient<IDataQualityEvaluationService, DataQualityEvaluationService>();
        services.TryAddTransient<IManualQueryExecutionLogger, ManualQueryExecutionLogger>();
        services.TryAddTransient<IAppSettingsService, AppSettingsService>();
        services.TryAddTransient<IAppSettingsStager, AppSettingsService>();
        // Applies the stored base URL and LLM configuration once the host has started (Beacon.Api's StoredSettingsStartupApplier).
        services.TryAddTransient<StoredSettingsApplier>();
        services.TryAddSingleton<StoredSettingsConvergence>();
        services.TryAddTransient<IQueryVersionService, QueryVersionService>();

        // Saved queries exposed as MCP tools: callable-tool lookup (project-scoped) and the read-only executor.
        services.TryAddTransient<ISavedQueryToolSource, SavedQueryToolSource>();
        services.TryAddTransient<ISavedQueryToolExecutor, SavedQueryToolExecutor>();
        services.TryAddTransient<ISavedQueryRunner, SavedQueryToolExecutor>();
        services.TryAddTransient<IQueryApprovalService, QueryApprovalService>();

        // Change approvals of migration jobs, Custom SQL rules, recipient destinations and the LLM connection: submission
        // (as the request's caller), decisions, and the delivery gate.
        services.TryAddScoped<ChangeApprovals.ChangeApprovalSubmitter>();
        services.TryAddScoped<ChangeApprovals.RecipientChangeApproval>();
        services.TryAddScoped<ChangeApprovals.LlmConnectionApproval>();
        services.TryAddSingleton<ChangeApprovals.ILlmConnectionLock, ChangeApprovals.DatabaseLlmConnectionLock>();
        services.TryAddScoped<ChangeApprovals.CustomSqlChangeApproval>();
        services.TryAddTransient<ChangeApprovals.RecipientDeliveryApproval>();
        services.TryAddTransient<ChangeApprovals.MigrationJobApprovalState>();
        services.TryAddTransient<ChangeApprovals.IChangeApprovalService, ChangeApprovals.ChangeApprovalService>();

        services.TryAddTransient(typeof(IBeaconScheduler), configurationOptions.BeaconScheduler!);

        // MCP settings provider (cached reads for MCP tool configuration)
        services.TryAddTransient<IMcpSettingsProvider, McpSettingsProvider>();
        services.TryAddTransient<ISharedSourceSettings, SharedSourceSettings>();

        // Content-retention policy (the lock) plus the EF belt that enforces it on every Mcp* write.
        // The interceptor holds no state; it resolves the settings provider lazily per SaveChanges.
        services.TryAddTransient<IContentRetentionPolicy, ContentRetentionPolicy>();
        services.TryAddSingleton<ContentRetentionInterceptor>();

        // API key service (always registered — used for stateless API authentication)
        services.TryAddSingleton<Services.Security.ApiKeyLastUsedThrottle>();
        services.TryAddTransient<Services.Security.IApiKeyService, Services.Security.ApiKeyService>();

        // Query guardrail service (always registered — enforces read-only access and PII detection)
        services.TryAddTransient<Services.Security.IQueryGuardrailService, Services.Security.QueryGuardrailService>();

        // AST-based read-only validator (defense-in-depth after the regex guardrail; §1.5).
        // Shared by MCP tools, query-builder step persistence, and step execution.
        services.TryAddSingleton<SqlReadOnlyAstValidator>();

        // SQL schema validator (pre-execution column check; moved from Beacon.MCP so the shared
        // AskSqlPipeline in Beacon.AI can depend on it without referencing Beacon.MCP, §2.4).
        services.TryAddSingleton<SqlSchemaValidator>();

        // Semantic linter (undeclared join / fan-out aggregate / GROUP BY mismatch; pure, stateless —
        // no I/O, so a plain singleton is enough, matching the other validators on this surface).
        services.TryAddSingleton<SqlSemanticLinter>();

        // The one pre-execution gate every MCP SQL path runs (guardrail → AST → schema → lint → row limit).
        // Transient because IQueryGuardrailService is transient; the gate itself is stateless.
        services.TryAddTransient<ISqlExecutionGate, SqlExecutionGate>();

        // Host DbContext exposure (ExposeDbContext). Always registered: with no registrations the registry is empty
        // and every host-managed row is refused. The connection resolver is the single point where stored
        // connection data becomes a live string; host references resolve against the host configuration.
        services.TryAddSingleton<HostData.IXmlDocumentationProvider, HostData.AssemblyXmlDocumentationProvider>();
        services.TryAddSingleton<HostData.IHostDataSourceRegistry, HostData.HostDataSourceRegistry>();
        services.TryAddTransient<HostData.IHostDataSourceGuard, HostData.HostDataSourceGuard>();
        services.TryAddTransient<HostData.IDataSourceConnectionResolver>(x => ConnectionResolver(x, configuration));
        services.TryAddTransient<HostData.IDataSourceDefinitionReader>(x => ConnectionResolver(x, configuration));
        services.TryAddTransient<Services.Providers.DataSourceTransportStatus>();
        services.TryAddTransient<IConfiguredDatabaseResolver, ConfiguredDatabaseResolver>();
        services.TryAddTransient<HostData.IHostProjectResolver, HostData.HostProjectResolver>();
        services.TryAddTransient<HostData.IHostSyncLock, HostData.DatabaseHostSyncLock>();
        services.TryAddTransient(x => ActivatorUtilities.CreateInstance<HostData.HostDataSourceSynchronizer>(x, configuration));

        // Host documentation import (ExposeDocs). Paths resolve against the host content root; the doc-chunk
        // indexer is optional (registered by AddBeaconAI) and only adds embeddings on top of keyword search.
        services.TryAddTransient(x => new HostDocs.HostDocsSynchronizer(
            x.GetRequiredService<IDbContextFactory<BeaconContext>>(),
            x.GetRequiredService<HostData.IHostProjectResolver>(),
            x.GetServices<HostDocs.HostDocsRegistration>(),
            x.GetRequiredService<IMcpSettingsProvider>(),
            x.GetService<IDocChunkIndexingService>(),
            x.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory,
            x.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HostDocs.HostDocsSynchronizer>>()));
        services.TryAddTransient<HostDocs.IProjectBriefService, HostDocs.ProjectBriefService>();

        // API-key issuance (Beacon:ApiKeys): every new key expires, within a configurable maximum lifetime, and keys of an
        // owner who stopped signing in can be refused. ValidateOnStart so a value out of range fails the host at boot.
        services.AddOptions<Configuration.ApiKeyOptions>()
            .Bind(configuration.GetSection(Configuration.ApiKeyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Configuration.ApiKeyOptions>, Configuration.ApiKeyOptionsValidator>();

        // Sign-in sessions (Beacon:Authentication:Session): how often a session is checked against the user store and its
        // absolute lifetime. ValidateOnStart so a value out of range fails the host at boot. The store reads the user a
        // session names and changes a user's security stamp. The request's verified password stamp is what its sign-in
        // must still find stored.
        services.AddOptions<Configuration.SessionLifetimeOptions>()
            .Bind(configuration.GetSection(Configuration.SessionLifetimeOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.SessionLifetimeOptions>, Configuration.SessionLifetimeOptionsValidator>());
        services.TryAddTransient<IUserSessionStore, UserSessionStore>();
        services.TryAddScoped<SessionStampExpectation>();

        // Where Beacon's authentication middlewares apply (Beacon:Authentication:AdditionalPaths): /beacon and the listed
        // prefixes. ValidateOnStart so an entry that is not a path prefix, or is '/' (every host route), fails the host at boot.
        services.AddOptions<Configuration.AuthenticationPathOptions>()
            .Bind(configuration.GetSection(Configuration.AuthenticationPathOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.AuthenticationPathOptions>, Configuration.AuthenticationPathOptionsValidator>());
        services.TryAddSingleton<Configuration.AuthenticationPathPrefixes>();

        // Row cap of the statements Beacon runs for its own features (Beacon:Query). ValidateOnStart so a cap out of range
        // fails the host at boot.
        services.AddOptions<Configuration.QueryExecutionOptions>()
            .Bind(configuration.GetSection(Configuration.QueryExecutionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.QueryExecutionOptions>, Configuration.QueryExecutionOptionsValidator>());

        // How exported reports are written (Beacon:Reports).
        services.AddOptions<Configuration.ReportOptions>()
            .Bind(configuration.GetSection(Configuration.ReportOptions.SectionName));

        // Subscription limits (Beacon:Subscriptions): the shortest schedule interval a save accepts and the largest file a
        // delivery attaches. ValidateOnStart so a limit out of range fails the host at boot.
        services.AddOptions<Configuration.SubscriptionOptions>()
            .Bind(configuration.GetSection(Configuration.SubscriptionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.SubscriptionOptions>, Configuration.SubscriptionOptionsValidator>());
        services.TryAddSingleton<ISubscriptionScheduleCheck, SubscriptionScheduleCheck>();

        // Data-source transport (Beacon:DataSources:Transport): a connection must authenticate the server over TLS, as each
        // connector's inspector judges it; the options decide what happens to a stored source that does not.
        services.AddOptions<Configuration.DataSourceTransportOptions>()
            .Bind(configuration.GetSection(Configuration.DataSourceTransportOptions.SectionName));
        // Driver options and hosts (Beacon:DataSources): each connector's option inspector, and the optional host list.
        services.AddOptions<Configuration.DataSourceConnectionOptions>()
            .Bind(configuration.GetSection(Configuration.DataSourceConnectionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.DataSourceConnectionOptions>, Configuration.DataSourceConnectionOptionsValidator>());
        services.TryAddSingleton<Services.Providers.IDataSourceTransportPolicy, Services.Providers.DataSourceTransportPolicy>();
        // The declared engine checked against the server on create, a changed connection or engine, and the connection test.
        services.TryAddSingleton<Services.Providers.IDatabaseEngineVerifier, Services.Providers.DatabaseEngineVerifier>();
        // The host data sources' read-only login, checked for write access once per startup sync (a Warning only).
        services.TryAddTransient<HostData.IHostReadOnlyLoginCheck, HostData.HostReadOnlyLoginCheck>();

        // Data migrations (Beacon:Migrations): off unless the host turns them on, and their own row budget. ValidateOnStart
        // so a budget out of range fails the host at boot.
        services.AddOptions<Configuration.MigrationOptions>()
            .Bind(configuration.GetSection(Configuration.MigrationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.MigrationOptions>, Configuration.MigrationOptionsValidator>());

        // Request bounds on Beacon's own routes (Beacon:RequestLimits). ValidateOnStart so a negative value fails the host at boot.
        services.AddOptions<Configuration.RequestLimitsOptions>()
            .Bind(configuration.GetSection(Configuration.RequestLimitsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.RequestLimitsOptions>, Configuration.RequestLimitsOptionsValidator>());

        // AI actor think cooldown (Beacon:AiActors). ValidateOnStart so a cooldown out of range fails the host at boot.
        services.AddOptions<Configuration.AiActorOptions>()
            .Bind(configuration.GetSection(Configuration.AiActorOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.AiActorOptions>, Configuration.AiActorOptionsValidator>());

        // Forced metadata refreshes (Beacon:Metadata): a per-source cooldown. ValidateOnStart so a cooldown out of range
        // fails the host at boot.
        services.AddOptions<Configuration.MetadataRefreshOptions>()
            .Bind(configuration.GetSection(Configuration.MetadataRefreshOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.MetadataRefreshOptions>, Configuration.MetadataRefreshOptionsValidator>());
        services.TryAddSingleton<Services.Metadata.ForcedMetadataRefreshCooldown>();
        services.TryAddTransient<Services.Metadata.ForcedMetadataRefresh>();

        // Long work a caller starts and follows (a background metadata refresh, ...): one executor per task kind.
        services.TryAddScoped<BackgroundTasks.IBackgroundTaskService, BackgroundTasks.BackgroundTaskService>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<BackgroundTasks.IBackgroundTaskExecutor, Services.Metadata.MetadataRefreshTaskExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<BackgroundTasks.IBackgroundTaskExecutor, Handlers.DataMigration.MigrationRunTaskExecutor>());
        services.TryAddScoped<BackgroundTasks.BackgroundTaskRequesterPermissions>();
        services.TryAddTransient<BackgroundTasks.IBackgroundTaskHeartbeat, BackgroundTasks.BackgroundTaskHeartbeat>();

        // The stored sample values a metadata response carries: an Admin's only.
        services.TryAddTransient<Handlers.DataSources.MetadataSampleView>();

        // Rate limiter (singleton so the in-memory sliding windows are shared across requests)
        services.TryAddSingleton<Services.Security.RateLimiter>();

        // Request limits on Beacon's routes and the password lockout (Beacon:RateLimits). ValidateOnStart so a limit out of
        // range fails the host at boot. The API and the MCP server keep the counts (Beacon.Api, Beacon.MCP); the lockout is
        // stored on the user.
        services.AddOptions<Configuration.RateLimitOptions>()
            .Bind(configuration.GetSection(Configuration.RateLimitOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.RateLimitOptions>, Configuration.RateLimitOptionsValidator>());

        // Password sign-in through the login form (Beacon:UserManagement:LocalLogin), bound onto the code-configured
        // options whether or not user management is on: the login endpoint and the password providers read it. Binding
        // appends to a list, so a host that already bound the section onto its options would list every break-glass name
        // twice: the names are de-duplicated (trimmed, case included, as they are compared) before they are validated.
        var localLogin = configurationOptions.UserManagement.LocalLogin;
        configuration.GetSection(LocalLoginOptions.SectionName).Bind(localLogin);
        localLogin.BreakGlassUserNames = localLogin.BreakGlassUserNames
            .DistinctBy(x => x?.Trim() ?? string.Empty, StringComparer.Ordinal)
            .ToList();
        localLogin.Validate();

        // While local login is off, the last word on every password sign-in, whichever provider authenticated it: only a
        // stored internal break-glass user passes.
        services.TryAddScoped<PasswordSignInGate>();

        // User Management services (opt-in feature). While it is off, stand-ins refuse every call, so the handlers that
        // need the user store still resolve; code that falls back without a user store reads it through OrNull().
        if (!configurationOptions.UserManagement.Enabled)
        {
            services.TryAddTransient<IRoleService, DisabledRoleService>();
            services.TryAddTransient<IUserManagementService, DisabledUserManagementService>();

            // Constructing it writes nothing: only the setup endpoints announce it, and they never do while user management is off.
            services.TryAddSingleton<Services.Security.FirstRunSetupToken>();
        }

        if (configurationOptions.UserManagement.Enabled)
        {
            services.TryAddSingleton<Services.Security.IPasswordHasher, Services.Security.PasswordHasher>();
            services.TryAddTransient<IRoleService, RoleService>();
            services.TryAddTransient<IUserManagementService, UserManagementService>();

            // First-run setup needs a secret: the code-configured one, else Beacon:UserManagement:SetupToken, else one
            // generated per process and written once to the console while no super admin exists (FirstRunSetupToken).
            if (string.IsNullOrWhiteSpace(configurationOptions.UserManagement.SetupToken))
            {
                configurationOptions.UserManagement.SetupToken = configuration["Beacon:UserManagement:SetupToken"];
            }

            var setupToken = configurationOptions.UserManagement.SetupToken?.Trim();
            if (!string.IsNullOrEmpty(setupToken) && setupToken.Length < Services.Security.FirstRunSetupToken.MinimumConfiguredLength)
            {
                throw new InvalidOperationException(
                    $"Beacon:UserManagement:SetupToken must be at least {Services.Security.FirstRunSetupToken.MinimumConfiguredLength} " +
                    "characters. Leave it unset to have Beacon generate one at startup.");
            }

            services.TryAddSingleton<Services.Security.FirstRunSetupToken>();

            // Register database-backed authentication provider
            if (configurationOptions.UserManagement.AllowInternalUsers)
            {
                // If JWT is also configured, use hybrid provider
                if (configurationOptions.Authentication.Jwt?.ExternalLoginEndpoint != null)
                {
                    services.TryAddScoped<IBeaconAuthenticationProvider, Authentication.Providers.HybridAuthenticationProvider>();
                }
                else
                {
                    services.TryAddScoped<IBeaconAuthenticationProvider, Authentication.Providers.DatabaseAuthenticationProvider>();
                }
            }

            // Register database-backed authorization provider
            services.TryAddScoped<IBeaconAuthorizationProvider, Authorization.Providers.DatabaseAuthorizationProvider>();
        }

        // Last-resort fallbacks — no explicit provider and no user management.
        // Authorization: allow all (backward compatible). Authentication: always fails (requires explicit configuration).
        services.TryAddScoped<IBeaconAuthorizationProvider, DefaultAuthorizationProvider>();
        services.TryAddScoped<IBeaconAuthenticationProvider, DefaultAuthenticationProvider>();

        // AI-backed dependencies of handlers that Core scans in unconditionally. Registered only
        // when AI is off: AddBeaconAI registers IMcpEvalService with TryAddTransient, so an
        // unconditional TryAdd here would run first and permanently shadow the real implementation.
        if (!configurationOptions.UseAI)
        {
            services.TryAddSingleton<ILlmConnectionTester, AiDisabledLlmConnectionTester>();
            services.TryAddTransient<IMcpEvalService, AiDisabledMcpEvalService>();
        }

        // Registered whatever UseAI says. AddBeaconAI replaces it with the installed providers in either call order; a host
        // that sets UseAI without calling AddBeaconAI keeps this one, so the catalog says AI is off and no provider pick
        // is refused.
        services.TryAddSingleton<ILlmProviderCatalog, AiDisabledLlmProviderCatalog>();

        // Embed token service (HS256 mint/validate for embeddable Beacon integrations). Obsolete: the embed routes use
        // Beacon:Embed and its own codec; kept registered for hosts that resolve it until 6.0.
        services.Configure<EmbedTokenOptions>(configuration.GetSection("Beacon:EmbedToken"));
        services.AddSingleton<IValidateOptions<EmbedTokenOptions>, EmbedTokenOptionsValidator>();

        // Embedding saved queries (Beacon:Embed): off by default, a deployment lock. ValidateOnStart so an enabled host
        // without a strong signing key (or with the encryption key as one) fails at boot; the message never holds a key.
        services.AddOptions<EmbedOptions>()
            .Bind(configuration.GetSection(EmbedOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmbedOptions>>(new EmbedOptionsValidator(
            encryptionKey,
            PreviousEncryptionKeys(configuration),
            configuration.GetSection("Beacon:EmbedToken")["SigningKey"])));
        services.TryAddSingleton<EmbedTokenCodec>();
        services.TryAddScoped<EmbedGrantResolver>();
        services.TryAddSingleton<EmbedRenderLimiter>();
        services.TryAddSingleton<EmbedAuditLimiter>();

        // Deployment-level MCP locks and ceilings (Beacon:Mcp). An absent section = no locks, no ceilings.
        // ValidateOnStart so a non-positive ceiling fails the host at boot, not on the first query.
        services.AddOptions<Configuration.McpDeploymentOptions>()
            .Bind(configuration.GetSection(Configuration.McpDeploymentOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Configuration.McpDeploymentOptions>, Configuration.McpDeploymentOptionsValidator>();

        // MCP audit retention purge (Beacon:Mcp:Audit:RetentionDays); a null window is a no-op. Scheduled by
        // the host as a Warp recurring job (Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs).
        services.TryAddTransient<IMcpAuditRetentionService, McpAuditRetentionService>();
        services.TryAddTransient<IMaintenanceHeartbeat, MaintenanceHeartbeatRecorder>();
        services.TryAddTransient<IRetentionScheduleCheck, RetentionScheduleCheck>();

        // Data retention (Beacon:Retention): one job over every registered IRetentionTarget (0 = keep forever, the
        // default: nothing is deleted unless the host sets a window) plus the content expiry of the content stores, content first, each step within its share of the run's time budget. ValidateOnStart so an unknown target name, a
        // negative window or a keep-forever window under the lock fails the host at boot. A component adds a store with
        // one more TryAddEnumerable target. Scheduled by the host (the sample's Warp job data-retention).
        services.AddOptions<Configuration.RetentionOptions>()
            .Bind(configuration.GetSection(Configuration.RetentionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.RetentionOptions>, Configuration.RetentionOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, SubscriptionRunsRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, NotificationsRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, AnomalyBaselinesRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, AnomalyEventsRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, AiActorExecutionsRetentionTarget>());

        // After the executions: a plan whose execution that target just deleted can go in the same run.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, AiActorPlansRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, MigrationRunsRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, McpEvalRunsRetentionTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTarget, ExecutionLogRetentionTarget>());
        services.TryAddTransient<IDataRetentionService, DataRetentionService>();

        // Under the retention lock the learning-signal window Admins edit at runtime resolves to at most Retention:Days.
        // Bound from the section directly: the retention validator reads the MCP options, so IOptions here would loop.
        services.AddOptions<Configuration.McpDeploymentOptions>()
            .PostConfigure(x => Configuration.RetentionLock.CapLearningSignals(
                x,
                configuration.GetSection(Configuration.RetentionOptions.SectionName).Get<Configuration.RetentionOptions>() ?? new Configuration.RetentionOptions()));

        // Telemetry (Beacon:Telemetry). An absent section = no content on spans.
        services.AddOptions<Configuration.BeaconTelemetryOptions>()
            .Bind(configuration.GetSection(Configuration.BeaconTelemetryOptions.SectionName));

        // JWT callers on /beacon/mcp (Beacon:Mcp:Callers). ValidateOnStart so a system entry with both or neither of
        // ClientId/ObjectId fails the host at boot. TryAdd so a host can supply its own IMcpCallerMapper.
        services.AddOptions<Configuration.McpCallerOptions>()
            .Bind(configuration.GetSection(Configuration.McpCallerOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Configuration.McpCallerOptions>, Configuration.McpCallerOptionsValidator>();
        services.TryAddScoped<Mcp.IMcpCallerMapper, Mcp.ConfiguredMcpCallerMapper>();

        // The request's principal as Core reads it. A singleton (implementations read per call; the web one through the
        // async-local IHttpContextAccessor), so every lifetime can take it. Core's default serves no request; Beacon.Api
        // and Beacon.MCP replace only this default (TryUseRequestPrincipal), so a host's own principal stays, and a host's
        // UseRequestPrincipal replaces it whenever it runs.
        services.TryAddSingleton<IBeaconRequestPrincipal, NoRequestPrincipal>();

        // The request's caller as the owner/creator/assignee of resources and as an Admin (scoped: resolved once per
        // request through IBeaconRequestPrincipal — in a scope without a request, the run-as owner, the system or nobody).
        // TryAdd so a host can supply its own.
        services.TryAddScoped<IBeaconActorAccessor, BeaconActorAccessor>();
        services.TryAddScoped<ISystemWorkScope, Authorization.ProjectAccess.SystemWorkScope>();
        services.TryAddScoped<IRunAsScope, Authorization.ProjectAccess.RunAsScope>();
        services.TryAddSingleton<Authorization.ProjectAccess.UnmarkedSystemWorkLog>();
        services.TryAddSingleton<Authorization.ProjectAccess.OwnerRunsAsSystemLog>();

        // Project access (Beacon:Authorization): the evaluator is scoped and memoized per scope; the membership and
        // ceiling sources are host seams. TryAdd so a host can supply any of them. ValidateOnStart so restricting every
        // project without a membership source fails the host at boot.
        services.AddOptions<Configuration.ProjectAuthorizationOptions>()
            .Bind(configuration.GetSection(Configuration.ProjectAuthorizationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.ProjectAuthorizationOptions>, Configuration.ProjectAuthorizationOptionsValidator>());
        services.TryAddSingleton<IProjectMembershipSource, Authorization.ProjectAccess.DatabaseProjectMembershipSource>();
        services.TryAddSingleton<IRunAsCeilingSource, Authorization.ProjectAccess.DatabaseRunAsCeilingSource>();
        services.TryAddScoped<IProjectAccessEvaluator, Authorization.ProjectAccess.ProjectAccessEvaluator>();
        services.TryAddScoped<Authorization.ProjectAccess.IProjectResourceResolver, Authorization.ProjectAccess.ProjectResourceResolver>();
        services.TryAddScoped<Authorization.ProjectAccess.ProjectRequestAuthorizer>();
        services.TryAddScoped<IExecutionAccessCheck, Authorization.ProjectAccess.ExecutionAccessCheck>();

        // Row-level security (Beacon:RowSecurity): the lock is off by default, and ValidateOnStart refuses it while row
        // security cannot be enforced. Every statement that runs takes its context from the resolver, scoped like the
        // execution access check.
        services.AddOptions<Configuration.RowSecurityOptions>()
            .Bind(configuration.GetSection(Configuration.RowSecurityOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.RowSecurityOptions>, Configuration.RowSecurityOptionsValidator>());
        services.TryAddScoped<RowSecurity.IRowSecurityResolver, RowSecurity.RowSecurityResolver>();

        // Functional permissions (Beacon:Authorization:Roles, IdpRoles, SeparationOfDuties): the role each permission is
        // held through, identity-provider app roles mapped at sign-in, and separation of duties. ValidateOnStart so a
        // mapping onto a built-in role, or two permissions on one role, fails the host at boot. The permission accessor is
        // scoped (the caller's permissions are read once per request); TryAdd so a host can supply its own.
        services.AddOptions<Configuration.RoleMappingOptions>()
            .Bind(configuration.GetSection(Configuration.RoleMappingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Configuration.RoleMappingOptions>, Configuration.RoleMappingOptionsValidator>());
        services.TryAddScoped<IBeaconPermissionAccessor, BeaconPermissionAccessor>();
        services.TryAddScoped<IReviewerAuthority, ReviewerAuthority>();
        services.TryAddSingleton<BeaconPermissionLog>();
        services.TryAddScoped<Handlers.Users.UserAdministrationRules>();
        services.TryAddScoped<Handlers.DataMigration.MigrationJobAccess>();
        services.TryAddScoped<Handlers.DataMigration.MigrationWritePolicy>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
#pragma warning disable CS0618 // the obsolete embed token service stays registered until 6.0
        services.TryAddSingleton<IEmbedTokenService, EmbedTokenService>();
#pragma warning restore CS0618

        return new BeaconBuilder(services, configuration, configurationOptions);
    }

    public static void UseBeacon(IServiceProvider serviceProvider, bool createSchema = false)
    {
        using var scope = serviceProvider.CreateScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BeaconContext>>();
        using var context = contextFactory.CreateDbContext();

        // Get the schema name from the context, validated up front so a malformed identifier
        // is rejected before any DDL is issued — including Migrate() below, not just the
        // createSchema:true path.
        var schema = BeaconSchema.ValidateIdentifier(BeaconSchema.Resolve(context) ?? "beacon");

        // Ensure the schema exists before running migrations.
        // Schema names cannot be parameterized in DDL — the identifier was already validated above.
        if (createSchema)
        {
#pragma warning disable EF1002 // identifier validated by ValidateIdentifier above; DDL cannot take parameters
            context.Database.ExecuteSqlRaw(
                BeaconSchema.CreateSchemaStatement(context.Database.ProviderName, schema));
#pragma warning restore EF1002
        }

        context.Database.Migrate();

        // Settings hydration is intentionally not performed here — handlers
        // resolve IAppSettingsService on demand. Hydrating at startup would
        // either require an async entrypoint or block-on-async, both of which
        // we avoid.
    }

    // The connection resolver, which also reads stored connection definitions: one decrypt site for both.
    private static HostData.DataSourceConnectionResolver ConnectionResolver(IServiceProvider services, IConfiguration configuration)
    {
        return new HostData.DataSourceConnectionResolver(
            services.GetRequiredService<IEncryptionService>(),
            configuration,
            services.GetRequiredService<Services.Providers.IDataSourceTransportPolicy>(),
            services.GetRequiredService<BeaconConfiguration>(),
            services.GetRequiredService<ChangeApprovals.ConnectionDefinitionHasher>(),
            services.GetService<Microsoft.Extensions.Logging.ILogger<HostData.DataSourceConnectionResolver>>());
    }
}