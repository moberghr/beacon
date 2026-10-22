using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 of spec <c>sql-execution-gate</c>: the <c>query</c> tool now runs the schema-catalog gate and it is
/// BLOCKING — a hallucinated column is refused before anything reaches the provider. With an empty catalog
/// (a source never scanned) the gate reports Skipped and the tool refuses the statement. Real gate over the real validators
/// (guardrail included); data-source resolution over async-queryable doubles (§4.7).
/// </summary>
[TestFixture]
public class ProjectQueryToolSchemaGateTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;
    private const int CallerUserId = 17;

    private Mock<IDataSourceProvider> _provider = null!;
    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private List<McpQuerySignal> _signals = null!;
    private List<McpAuditLog> _auditLogs = null!;
    private RecordingSqlGate _gate = null!;
    private DataSourceType _warehouseType;
    private DatabaseEngineType? _warehouseEngine;
    private Beacon.Core.Services.Validation.IConfiguredDatabaseResolver _configuredDatabases = null!;
    private Beacon.Core.Configuration.McpDeploymentOptions? _deployment;
    private string? _hostManagedKey;
    private Beacon.Core.HostData.IHostDataSourceGuard? _hostGuard;

    [SetUp]
    public void SetUp()
    {
        _deployment = null;
        _hostManagedKey = null;
        _hostGuard = null;
        _provider = new Mock<IDataSourceProvider>();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["id"] = 1 }]
            });

        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _signals = [];
        _auditLogs = [];
        _warehouseType = DataSourceType.Database;
        _warehouseEngine = DatabaseEngineType.PostgreSQL;
        _configuredDatabases = TestConfiguredDatabases.None;
    }

    // The settings that apply are the data source's: the strictest guardrails of the live projects that link it.
    [Test]
    public async Task TheDataSourcesGuardrails_Apply_NotOnlyTheProjects()
    {
        WithScannedCatalog();
        var settingsProvider = SettingsProviderMock.Create(new McpSettingsData { MaxRowLimit = 1000 });
        var sharedSources = new Mock<ISharedSourceSettings>();
        sharedSources
            .Setup(x => x.GetEffectiveSettingsAsync(ProjectId, It.Is<IReadOnlyCollection<int>>(y => y.SequenceEqual(new[] { DataSourceId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { MaxRowLimit = 5 });

        var result = await CreateTool(settingsProvider, sharedSources.Object).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), "SELECT id FROM orders LIMIT 5", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // A max_rows of zero or below never removes the row cap: the effective cap is at least one row and lands in the SQL.
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public async Task NonPositiveMaxRows_IsClampedToOneRow_TheCapReachesTheSql(int maxRows)
    {
        WithScannedCatalog();

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", max_rows: maxRows, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), "SELECT id FROM orders LIMIT 1", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The gate gets the database the source's connection is configured for: a name in it runs, a name in another is refused.
    [TestCase("SELECT id FROM sales.public.orders", false)]
    [TestCase("SELECT id FROM hr.public.orders", true)]
    public async Task NameInAnotherDatabase_IsRefusedBeforeTheProvider_TheConfiguredOneRuns(string sql, bool refused)
    {
        _configuredDatabases = TestConfiguredDatabases.Of("sales");
        WithScannedCatalog();

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().Be(refused);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            refused ? Times.Never() : Times.Once());
        if (refused)
        {
            result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("another database");
        }
    }

    // With read-only enforcement off the database rule still holds: a database switch or SQL run from a string is refused
    // before the provider, and a write batch that stays in the configured database reaches it.
    [TestCase("USE HR; SELECT * FROM dbo.Customers", "USE is not allowed")]
    [TestCase("EXEC sp_executesql N'SELECT * FROM HR.dbo.Customers'", "sp_executesql is not allowed")]
    public async Task WithReadOnlyEnforcementOff_ADatabaseSwitchOrDynamicSql_IsRefusedBeforeTheProvider(string sql, string reason)
    {
        _warehouseEngine = DatabaseEngineType.MSSQL;
        _configuredDatabases = TestConfiguredDatabases.Of("Sales");
        _deployment = TestSqlGate.Unlocked;
        WithScannedCatalog();
        var settingsProvider = SettingsProviderMock.Create(new McpSettingsData { EnforceReadOnly = false });

        var refused = await CreateTool(settingsProvider).ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (refused.IsError ?? false).Should().BeTrue();
        refused.Content.OfType<TextContentBlock>().Single().Text.Should().Contain(reason);
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Contain(reason);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        const string batch = "UPDATE Sales.dbo.Customers SET tier = 'gold' WHERE id = 1; SELECT id FROM dbo.Customers";
        var allowed = await CreateTool(settingsProvider).ExecuteAsync(datasource_id: DataSourceId, sql: batch, cancellationToken: CancellationToken.None);

        (allowed.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), batch, It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Under the default deployment lock (Beacon:Mcp:ForceReadOnly) the tool judges SQL read-only whatever the settings it
    // is handed say, so the same write batch never reaches the provider.
    [Test]
    public async Task UnderTheDefaultReadOnlyLock_AWriteBatch_IsRefusedEvenWhenTheSettingsSayReadOnlyIsOff()
    {
        _warehouseEngine = DatabaseEngineType.MSSQL;
        _configuredDatabases = TestConfiguredDatabases.Of("Sales");
        WithScannedCatalog();
        var settingsProvider = SettingsProviderMock.Create(new McpSettingsData { EnforceReadOnly = false });
        const string batch = "UPDATE Sales.dbo.Customers SET tier = 'gold' WHERE id = 1; SELECT id FROM dbo.Customers";

        var refused = await CreateTool(settingsProvider).ExecuteAsync(datasource_id: DataSourceId, sql: batch, cancellationToken: CancellationToken.None);

        (refused.IsError ?? false).Should().BeTrue();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // The parser reads U+FFFF as the end of its input; the tool refuses SQL holding one (or any noncharacter or control
    // character) before the provider, with read-only enforcement on or off, and the refusal is audited.
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT 1 /* x */￿; COMMIT; BEGIN READ WRITE; DELETE FROM t; SELECT 1", false)]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT 1 --￿\nCOMMIT; DELETE FROM dbo.t; SELECT 1", false)]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT 1 /* x */￿ COMMIT", true)]
    [TestCase(DatabaseEngineType.MySQL, "SELECT 1 -- x￿\n; COMMIT; DELETE FROM t; SELECT 1", true)]
    public async Task ANoncharacterInTheSql_IsRefusedBeforeTheProvider(DatabaseEngineType engine, string sql, bool readOnlyOff)
    {
        _warehouseEngine = engine;
        WithScannedCatalog();
        Mock<IMcpSettingsProvider>? settingsProvider = null;
        if (readOnlyOff)
        {
            _deployment = TestSqlGate.Unlocked;
            settingsProvider = SettingsProviderMock.Create(new McpSettingsData { EnforceReadOnly = false });
        }

        var result = await CreateTool(settingsProvider).ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("noncharacter");
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Contain("noncharacter");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestCase(DataSourceType.BigQuery, "bigquery")]
    [TestCase(DataSourceType.Databricks, "databricks")]
    public async Task WarehouseWithoutEngineType_IsGatedInItsOwnDialect(DataSourceType type, string dialect)
    {
        _warehouseType = type;
        _warehouseEngine = null;
        WithScannedCatalog();

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT id FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _gate.Dialects.Should().Equal(dialect);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), "SELECT id FROM orders LIMIT 25", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task UnknownColumn_WithCatalog_IsRefused_AndProviderNeverCalled()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["orders"] = ["id", "customer_id", "total"]
        });

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT bogus FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().StartWith("Query validation failed: ");
        text.Should().Contain("Column 'bogus' does not exist");

        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a statement refused by the schema gate must never reach the connector");

        // §9.5 — the refusal is still a recorded signal, with the AST-resolved tables.
        _signals.Should().ContainSingle();
        _signals[0].IsSuccessful.Should().BeFalse();
        _signals[0].ExecutionError.Should().Contain("bogus");
        _signals[0].TablesUsed.Should().Contain("orders");
    }

    [Test]
    public async Task EmptyCatalog_OnAnOrdinarySource_IsRefused_AuditedAndSignalled()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT bogus FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be(ProjectQueryTool.NoMetadataMessage);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "without a catalog the schema check cannot run, so the statement never reaches the source");

        // §1.7 / §9.5 — the refusal is audited and signalled like every other `query` refusal.
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(ProjectQueryTool.NoMetadataMessage);
        _signals.Should().ContainSingle();
        _signals[0].IsSuccessful.Should().BeFalse();
        _signals[0].ExecutionError.Should().Be(ProjectQueryTool.NoMetadataMessage);
        _signals[0].TablesUsed.Should().Contain("orders");
    }

    [Test]
    public async Task EmptyCatalog_OnAHostDataSource_IsNotRefused_ItsAllowListDecides()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        _hostManagedKey = "efcore:Netgiro";
        var hostGuard = new Mock<Beacon.Core.HostData.IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check("efcore:Netgiro", It.IsAny<string>()))
            .Returns(new Beacon.Core.HostData.HostPolicyResult(true, null, []));
        _hostGuard = hostGuard.Object;

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT id FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        hostGuard.Verify(x => x.Check("efcore:Netgiro", It.IsAny<string>()), Times.Once);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task KnownColumns_WithCatalog_Execute_AndTablesResolvedFromAst()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["orders"] = ["id", "customer_id", "total"],
            ["customers"] = ["id", "name"]
        });

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId,
            sql: "WITH recent AS (SELECT * FROM orders o) SELECT c.name FROM recent JOIN customers c ON c.id = recent.customer_id",
            cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _signals.Should().ContainSingle();
        _signals[0].TablesUsed.Should().Contain("orders").And.Contain("customers");
        _signals[0].TablesUsed.Should().NotContain("recent", "a CTE name is not a table");
    }

    [Test]
    public async Task ProjectMaxRowLimitOverride_CapsTheQuery_BelowTheRequestedAndGlobalLimits()
    {
        // T-F002 / SF-F001: the tool resolves settings for the AUTHORIZED project, not the global row. Global
        // allows 1000 rows, project 42 is capped at 10, the caller asks for 25 — the project cap wins.
        WithScannedCatalog();
        var settingsProvider = SettingsProviderMock.Create(
            new McpSettingsData { MaxRowLimit = 1000 },
            projectSettings: new Dictionary<int, McpSettingsData> { [ProjectId] = new McpSettingsData { MaxRowLimit = 10 } });

        var result = await CreateTool(settingsProvider).ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT id FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                "SELECT id FROM orders LIMIT 10",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the project's MaxRowLimit override caps the query below both the requested 25 and the global 1000");
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(ProjectId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.Is<int>(id => id != ProjectId), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("SELECT * FROM orders", "email")]
    [TestCase("SELECT * FROM orders", "customer_email")]
    [TestCase("SELECT * FROM orders", "email_address")]
    public async Task PiiResultColumns_AreMasked_EvenWhenTheSqlTextDoesNotNameThem(string sql, string resultColumn)
    {
        WithScannedCatalog();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { [resultColumn] = "ada@example.com", ["id"] = 1 }]
            });

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        text.Should().NotContain("ada@example.com");
        text.Should().Contain("a***m");
    }

    // SC7: the result key "contact" is not PII by name and the SQL-text match is "email" — only the executed SQL's
    // alias resolution masks it.
    [TestCase("SELECT email AS contact, id FROM orders")]
    [TestCase("SELECT upper(o.email) AS contact, o.id FROM orders o")]
    public async Task AnAliasedPiiColumn_IsMasked(string sql)
    {
        WithScannedCatalog();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["contact"] = "ada@example.com", ["id"] = 1 }]
            });

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        text.Should().NotContain("ada@example.com");
        text.Should().Contain("a***m");
    }

    [Test]
    public async Task ASuccessfulQuery_RecordsTheCallersBeaconUser_OnItsSignalAndAuditRow()
    {
        WithScannedCatalog();

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _signals.Should().ContainSingle().Which.UserId.Should().Be(CallerUserId);
        _auditLogs.Should().ContainSingle().Which.Should().Match<McpAuditLog>(x => x.UserId == CallerUserId && x.ErrorMessage == null);
    }

    [Test]
    public async Task ARefusedQuery_RecordsTheCallersBeaconUser_OnItsSignalAndAuditRow()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { ["orders"] = ["id"] });

        var blocked = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT bogus FROM orders", cancellationToken: CancellationToken.None);
        var failedEarly = await CreateTool().ExecuteAsync(cancellationToken: CancellationToken.None);

        (blocked.IsError ?? false).Should().BeTrue();
        (failedEarly.IsError ?? false).Should().BeTrue();
        _signals.Should().HaveCount(2).And.OnlyContain(x => x.UserId == CallerUserId);
        _auditLogs.Should().HaveCount(2).And.OnlyContain(x => x.UserId == CallerUserId && x.ErrorMessage != null);
    }

    // A database error that quotes a row value: the stored signal always holds it redacted; the caller and the audit row get
    // it redacted while PII detection is on, as it was otherwise.
    [TestCase(true)]
    [TestCase(false)]
    public async Task AFailureQuotingAValue_IsRedactedOnTheSignal_AndForTheCallerAndAuditWhileDetectionIsOn(bool piiDetectionOn)
    {
        const string error = "22P02: invalid input syntax for type integer: \"Ivan Horvat\"";
        const string redacted = "Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.";
        WithScannedCatalog();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = false, ErrorMessage = error });
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnablePiiDetection = piiDetectionOn });

        var result = await CreateTool(settings).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        var expected = piiDetectionOn ? redacted : error;
        (result.IsError ?? false).Should().BeTrue();
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text)).Should().Be($"Query execution failed: {expected}");
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected);
        _signals.Should().ContainSingle().Which.ExecutionError.Should().Be(redacted);
    }

    // A host data source's result whose columns the database named otherwise than the policy derived says how many were
    // masked for it.
    [Test]
    public async Task AResultWithColumnsMaskedForTheirName_SaysHowMany()
    {
        WithScannedCatalog();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["id"] = "***" }],
                Metadata = new Dictionary<string, object?> { [Beacon.Core.HostData.HostResultMetadata.RenamedMaskedColumns] = 1 }
            });

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text))
            .Should().Contain("*1 column(s) of this host data source result were masked because the database named them differently than the host policy checked them.*");
    }

    // The recorded MCP limits on the query tool: the data source's resolved settings (the strictest of the projects that
    // link it) set the statement timeout, the result size budget and the concurrent-query limit.
    [Test]
    public async Task TheStatement_RunsWithinTheDataSourcesResolvedTimeout()
    {
        WithScannedCatalog();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns<DataSource, string, Dictionary<string, object?>, CancellationToken>(async (_, _, _, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                return new ProviderQueryResult { Success = true, Rows = [] };
            });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { StatementTimeoutSeconds = 1 })).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the resolved 1 s timeout cancelled the statement, not the 30 s default");
        (result.IsError ?? false).Should().BeTrue();
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task AResultOverTheResolvedMaxResultBytes_ReturnsFewerRows_TruncatedBySize()
    {
        WithScannedCatalog();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = Enumerable.Range(1, 40)
                    .Select(x => new Dictionary<string, object?> { ["id"] = x, ["note"] = new string('x', 500) })
                    .ToList()
            });

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { MaxResultBytes = 4096 })).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id, note FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse("a result over the size budget is truncated, never failed");
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("_Further rows omitted — response size budget reached._");
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("truncated").GetBoolean().Should().BeTrue();
        structured.GetProperty("rows_omitted_for_size").GetBoolean().Should().BeTrue();
        structured.GetProperty("rows").GetArrayLength().Should().BeInRange(1, 39);
        (System.Text.Encoding.UTF8.GetByteCount(result.Content.OfType<TextContentBlock>().Single().Text) + System.Text.Encoding.UTF8.GetByteCount(structured.GetRawText()))
            .Should().BeLessThanOrEqualTo(4096, "the text and the structured payload together stay within MaxResultBytes");
        _auditLogs.Should().ContainSingle().Which.ResultRowCount.Should().Be(40);
    }

    [Test]
    public async Task AtTheConcurrencyLimit_TheCallIsRefusedBeforeTheProvider_AndAuditedWithASignal()
    {
        WithScannedCatalog();
        var limiter = new McpQueryConcurrencyLimiter();
        using var running = limiter.TryAcquire($"user:{CallerUserId}", 2);
        using var another = limiter.TryAcquire($"user:{CallerUserId}", 2);

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { MaxConcurrentQueriesPerKey = 2 }), limiter: limiter).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be(McpQueryConcurrencyLimiter.RefusalMessage);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _auditLogs.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Tool = "query", ErrorMessage = McpQueryConcurrencyLimiter.RefusalMessage, ProjectId = (int?)ProjectId, DataSourceId = (int?)DataSourceId },
            x => x.ExcludingMissingMembers());
        _signals.Should().ContainSingle().Which.ExecutionError.Should().Be(McpQueryConcurrencyLimiter.RefusalMessage);
        limiter.InFlight($"user:{CallerUserId}").Should().Be(2, "the refused call took no slot");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheSlot_IsHeldWhileTheStatementRuns_AndReleasedAfterSuccessOrFailure(bool providerThrows)
    {
        WithScannedCatalog();
        var limiter = new McpQueryConcurrencyLimiter();
        var inFlightDuringRun = -1;
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, _, _, _) =>
            {
                inFlightDuringRun = limiter.InFlight($"user:{CallerUserId}");
                return providerThrows
                    ? throw new InvalidOperationException("connection reset")
                    : Task.FromResult(new ProviderQueryResult { Success = true, Rows = [new Dictionary<string, object?> { ["id"] = 1 }] });
            });

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { MaxConcurrentQueriesPerKey = 1 }), limiter: limiter).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().Be(providerThrows);
        inFlightDuringRun.Should().Be(1);
        limiter.InFlight($"user:{CallerUserId}").Should().Be(0);
    }

    // MaxExplainCost: the gate's FinalSql is checked against the resolved limit before it runs; a refusal never reaches the
    // provider and is audited and signalled like every other refusal.
    [Test]
    public async Task ACostOverTheLimit_IsRefusedBeforeTheProvider_AndAuditedWithASignal()
    {
        WithScannedCatalog();
        const string refusal = "The query's estimated cost (5000) exceeds this project's limit (100). Narrow the query, add filters, or ask an administrator to raise MaxExplainCost.";
        var costLimit = CostLimit(new QueryCostCheck(refusal, null));

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { MaxExplainCost = 100m }), costLimit: costLimit.Object).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be(refusal);
        costLimit.Verify(
            x => x.CheckAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains("LIMIT")), It.Is<IReadOnlyDictionary<string, object?>>(y => y.Count == 0), 100m, It.IsAny<CancellationToken>()),
            Times.Once,
            "the gate's FinalSql (row cap written in) is what is estimated, with the resolved limit");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _auditLogs.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Tool = "query", ErrorMessage = refusal, ProjectId = (int?)ProjectId, DataSourceId = (int?)DataSourceId },
            x => x.ExcludingMissingMembers());
        _signals.Should().ContainSingle().Which.ExecutionError.Should().Be(refusal);
    }

    [Test]
    public async Task ACostWithinTheLimit_Runs()
    {
        WithScannedCatalog();
        var costLimit = CostLimit(QueryCostCheck.Pass);

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { MaxExplainCost = 100m }), costLimit: costLimit.Object).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task AnEstimateThatFailed_FailsTheCallAsAFailingStatement_WithoutRunningIt()
    {
        WithScannedCatalog();
        var costLimit = CostLimit(new QueryCostCheck(null, "canceling statement due to statement timeout"));

        var result = await CreateTool(sharedSources: Shared(new McpSettingsData { MaxExplainCost = 100m }), costLimit: costLimit.Object).ExecuteAsync(datasource_id: DataSourceId, sql: "SELECT id FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("Query execution failed: canceling statement due to statement timeout");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be("canceling statement due to statement timeout");
    }

    private static Mock<IQueryCostLimit> CostLimit(QueryCostCheck check)
    {
        var costLimit = new Mock<IQueryCostLimit>();
        costLimit
            .Setup(x => x.CheckAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(check);

        return costLimit;
    }

    private static ISharedSourceSettings Shared(McpSettingsData settings)
    {
        var sharedSources = new Mock<ISharedSourceSettings>();
        sharedSources
            .Setup(x => x.GetEffectiveSettingsAsync(ProjectId, It.Is<IReadOnlyCollection<int>>(y => y.SequenceEqual(new[] { DataSourceId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        return sharedSources.Object;
    }

    // The catalog of a scanned source holding the tables these tests read: `query` refuses a source with none.
    private void WithScannedCatalog()
    {
        string[] orders = ["id", "note", "email", "customer_email", "email_address", "customer_id", "total"];
        string[] customers = ["id", "name", "tier"];
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["orders"] = new(orders, StringComparer.OrdinalIgnoreCase),
            ["public.orders"] = new(orders, StringComparer.OrdinalIgnoreCase),
            ["customers"] = new(customers, StringComparer.OrdinalIgnoreCase),
            ["dbo.customers"] = new(customers, StringComparer.OrdinalIgnoreCase)
        });
    }

    private void WithCatalog(Dictionary<string, HashSet<string>> catalog)
    {
        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog);
    }

    private ProjectQueryTool CreateTool(Mock<IMcpSettingsProvider>? settingsProvider = null, ISharedSourceSettings? sharedSources = null, McpQueryConcurrencyLimiter? limiter = null, IQueryCostLimit? costLimit = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SchemaGateTestContext(_signals, _auditLogs, _warehouseType, _warehouseEngine, _hostManagedKey));

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(_provider.Object);

        settingsProvider ??= SettingsProviderMock.Create();

        var projectContext = new McpProjectContext { UserId = CallerUserId, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] };
        var guardrail = new QueryGuardrailService();
        _gate = new RecordingSqlGate(TestSqlGate.Create(guardrail, _hostGuard, _deployment));

        var auditService = new McpAuditService(factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog());
        var signalService = new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance);

        return new ProjectQueryTool(
            factory.Object,
            providerFactory.Object,
            guardrail,
            _gate,
            _configuredDatabases,
            TestRowSecurity.Resolver,
            _knowledgeGraph.Object,
            sharedSources ?? SettingsProviderMock.Unshared(settingsProvider.Object),
            projectContext,
            auditService,
            signalService,
            limiter ?? new McpQueryConcurrencyLimiter(),
            costLimit ?? TestQueryCostLimit.Passing,
            NullLogger<ProjectQueryTool>.Instance);
    }

    /// <summary>Data-source resolution over async-queryable doubles plus signal capture — no DB (§4.7).</summary>
    private sealed class SchemaGateTestContext : BeaconContext
    {
        private static readonly DbContextOptions<SchemaGateTestContext> Options =
            new DbContextOptionsBuilder<SchemaGateTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpQuerySignal>> _signalSet = new();
        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();
        private readonly DataSourceType _warehouseType;
        private readonly DatabaseEngineType? _warehouseEngine;
        private readonly string? _hostManagedKey;

        public SchemaGateTestContext(
            List<McpQuerySignal> signals,
            List<McpAuditLog> auditLogs,
            DataSourceType warehouseType,
            DatabaseEngineType? warehouseEngine,
            string? hostManagedKey)
            : base(Options, "beacon")
        {
            _warehouseType = warehouseType;
            _warehouseEngine = warehouseEngine;
            _hostManagedKey = hostManagedKey;
            _signalSet.Setup(x => x.Add(It.IsAny<McpQuerySignal>()))
                .Callback<McpQuerySignal>(signals.Add);
            _auditSet.Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(auditLogs.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpQuerySignal))
            {
                return (DbSet<TEntity>)(object)_signalSet.Object;
            }

            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_auditSet.Object;
            }

            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>
                {
                    new()
                    {
                        Id = DataSourceId,
                        Name = "warehouse",
                        DataSourceType = _warehouseType,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = _warehouseEngine,
                        HostManagedKey = _hostManagedKey
                    }
                });
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<ProjectDataSource>
                {
                    new() { ProjectId = ProjectId, DataSourceId = DataSourceId }
                });
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private static DbSet<T> BuildSet<T>(List<T> data) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
            return set.Object;
        }
    }
}
