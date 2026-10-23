using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Beacon.Tests.Common;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Common;

/// <summary>
/// <see cref="QueryService"/> and <see cref="GatedSqlRunner"/> wired the way the host wires them: statements run through
/// the real execution gate and the real <see cref="DatabaseProvider"/>, which opens connections through
/// <c>DbConnectionFactory</c> — so a test registers a recording connection there. No database (§4.7).
/// </summary>
internal static class TestQueryServices
{
    /// <summary><c>Beacon:Query</c> as a host that sets nothing binds it: the defaults.</summary>
    public static IOptions<QueryExecutionOptions> QueryOptions { get; } = Options.Create(new QueryExecutionOptions());

    public static IDataSourceConnectionResolver Connections()
    {
        var connections = new Mock<IDataSourceConnectionResolver>();
        connections
            .Setup(x => x.GetConnectionString(It.IsAny<Beacon.Core.Data.Entities.DataSource>()))
            .Returns("Host=unused;Database=unused");

        return connections.Object;
    }

    /// <summary>The real provider factory's lookup over one provider, for every data source type.</summary>
    public static IDataSourceProviderFactory Providers(IDataSourceProvider provider)
    {
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider);

        return providers.Object;
    }

    /// <param name="queryOptions"><c>Beacon:Query</c> in place of the defaults.</param>
    public static DatabaseProvider DatabaseProvider(
        IDataSourceConnectionResolver? connections = null,
        IHostDataSourceGuard? hostGuard = null,
        IConfiguredDatabaseResolver? configuredDatabases = null,
        QueryExecutionOptions? queryOptions = null)
    {
        return new DatabaseProvider(
            connections ?? Connections(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            hostGuard ?? new HostDataSourceGuard(new TestHostDataSources()),
            configuredDatabases ?? TestConfiguredDatabases.None,
            NullLogger<DatabaseProvider>.Instance,
            queryOptions == null ? QueryOptions : Options.Create(queryOptions));
    }

    /// <param name="statementTimeout">The runner's own statement timeout in place of the default.</param>
    public static GatedSqlRunner Runner(
        IDataSourceConnectionResolver? connections = null,
        IHostDataSourceGuard? hostGuard = null,
        IConfiguredDatabaseResolver? configuredDatabases = null,
        IDataSourceProviderFactory? providers = null,
        ISqlExecutionGate? gate = null,
        TimeSpan? statementTimeout = null,
        ISqlExecutionAudit? executionAudit = null)
    {
        var guard = hostGuard ?? new HostDataSourceGuard(new TestHostDataSources());
        var databases = configuredDatabases ?? TestConfiguredDatabases.None;

        return new GatedSqlRunner(
            gate ?? TestSqlGate.Create(hostGuard: guard),
            providers ?? Providers(DatabaseProvider(connections, guard, databases)),
            databases,
            QueryOptions,
            executionAudit: executionAudit)
        {
            StatementTimeout = statementTimeout ?? TimeSpan.FromSeconds(QueryOptions.Value.StatementTimeoutSeconds)
        };
    }

    /// <param name="gate">The execution gate in place of the real one built here — a <see cref="RecordingSqlGate"/>, say;
    /// the steps' runner and the final query's join use it alike.</param>
    /// <param name="statementTimeout">The runner's own statement timeout in place of the default.</param>
    public static QueryService QueryService(
        IDbContextFactory<BeaconContext> contextFactory,
        IDataSourceConnectionResolver? connections = null,
        IHostDataSourceGuard? hostGuard = null,
        IConfiguredDatabaseResolver? configuredDatabases = null,
        IQueryVersionService? versions = null,
        BeaconConfiguration? configuration = null,
        IBeaconUserContext? userContext = null,
        IBeaconActorAccessor? actors = null,
        IExecutionAccessCheck? executionAccess = null,
        ISharedSourceSettings? sharedSourceSettings = null,
        IDataSourceProviderFactory? providers = null,
        IManualQueryExecutionLogger? executionLogger = null,
        int? executionRowCeiling = null,
        int? finalQueryTimeoutSeconds = null,
        ISqlExecutionGate? gate = null,
        TimeSpan? statementTimeout = null,
        ILogger<QueryService>? logger = null,
        ISqlExecutionAudit? executionAudit = null)
    {
        var guard = hostGuard ?? new HostDataSourceGuard(new TestHostDataSources());
        var databases = configuredDatabases ?? TestConfiguredDatabases.None;
        var executionGate = gate ?? TestSqlGate.Create(hostGuard: guard);

        return new QueryService(
            contextFactory,
            executionLogger ?? Mock.Of<IManualQueryExecutionLogger>(),
            logger ?? NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            versions ?? Mock.Of<IQueryVersionService>(),
            configuration ?? new BeaconConfiguration(),
            userContext ?? Mock.Of<IBeaconUserContext>(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            actors ?? TestProjectAccess.Actors(),
            executionAccess ?? TestProjectAccess.ExecutionAllowed(),
            TestRowSecurity.Resolver,
            executionGate,
            Runner(connections, guard, databases, providers, executionGate, statementTimeout, executionAudit),
            new StepSqlSaveCheck(contextFactory, executionGate, databases),
            new QueryGuardrailService(),
            sharedSourceSettings ?? DefaultSettings(),
            QueryOptions, TimeProvider.System)
        {
            ExecutionRowCeiling = executionRowCeiling ?? Constants.Query.MaxExecutionRows,
            FinalQueryTimeoutSeconds = finalQueryTimeoutSeconds ?? Constants.Query.StatementTimeoutSeconds
        };
    }

    /// <summary>
    /// <see cref="QueryVersionService"/> with a save check that accepts every step unless one is given, and the approval
    /// workflow as <paramref name="configuration"/> sets it (off by default); superseded requesters are told through
    /// <paramref name="notifier"/> when one is given.
    /// </summary>
    public static QueryVersionService VersionService(
        IDbContextFactory<BeaconContext> contextFactory,
        IBeaconUserContext? userContext = null,
        BeaconConfiguration? configuration = null,
        IStepSqlSaveCheck? saveCheck = null,
        IApprovalNotifier? notifier = null,
        ISecurityAuditRecorder? audit = null,
        TimeProvider? timeProvider = null)
    {
        return new QueryVersionService(
            contextFactory,
            userContext ?? Mock.Of<IBeaconUserContext>(),
            configuration ?? new BeaconConfiguration(),
            saveCheck ?? Mock.Of<IStepSqlSaveCheck>(),
            audit ?? new RecordingSecurityAudit(),
            NullLogger<QueryVersionService>.Instance,
            notifier,
            timeProvider);
    }

    /// <summary>
    /// <see cref="QueryApprovalService"/> deciding as <paramref name="actors"/>' caller (a stored user, "ext-ada", by
    /// default), with a save check that accepts every step unless one is given.
    /// </summary>
    public static QueryApprovalService ApprovalService(
        IDbContextFactory<BeaconContext> contextFactory,
        IBeaconActorAccessor? actors = null,
        BeaconConfiguration? configuration = null,
        IStepSqlSaveCheck? saveCheck = null,
        IQueryVersionService? versions = null,
        ISecurityAuditRecorder? audit = null)
    {
        return new QueryApprovalService(
            contextFactory,
            versions ?? Mock.Of<IQueryVersionService>(),
            actors ?? TestProjectAccess.Actors(),
            Mock.Of<IBeaconUserContext>(),
            saveCheck ?? Mock.Of<IStepSqlSaveCheck>(),
            configuration ?? new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } },
            audit ?? new RecordingSecurityAudit(),
            NullLogger<QueryApprovalService>.Instance);
    }

    /// <summary>The gate a final query on the in-memory join store passes, as QueryService asks it (read-only on).</summary>
    public static Func<string, SqlGateReport> JoinGate(int? maxRows = null)
    {
        var gate = TestSqlGate.Create();

        return x => gate.Evaluate(new SqlGateRequest(x, nameof(DatabaseEngineType.SQLite), true, false, null, null, false, null, false, maxRows, RowSecurity: TestRowSecurity.NotApplicable)
        {
            InMemoryJoin = true
        });
    }

    /// <summary>Every data source gets the default MCP settings (PII detection on).</summary>
    public static ISharedSourceSettings DefaultSettings(McpSettingsData? settings = null)
    {
        var shared = new Mock<ISharedSourceSettings>();
        shared
            .Setup(x => x.GetDataSourceSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => settings?.Clone() ?? new McpSettingsData());
        shared
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => settings?.Clone() ?? new McpSettingsData());

        return shared.Object;
    }
}
