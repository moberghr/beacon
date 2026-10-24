using Beacon.AI.Services.Knowledge;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.HostDocs;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// The rows of a host data source are masked by output POSITION in the database provider, which every path that runs
/// SQL on one shares: the provider's read paths (PostgreSQL's one-statement batch, Dapper, SQL Server's rolled-back
/// transaction, the row-capped read), the paged step preview, the query preview whose final query reads the step on the
/// in-memory join, and the MCP <c>query</c> tool. The database here labels the masked column with a name the host policy
/// did not derive — so a value matched by name would come back raw. Recording ADO.NET doubles, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class HostOutputMaskingPathTests
{
    private const string HostKey = "efcore:Netgiro";
    private const int HostDataSourceId = 7;
    private const int ProjectId = 42;
    private const string Ssn = "0101302989";

    // The name the database gives the masked column: not the alias (kt) the host policy derived for it.
    private const string ServerLabel = "kt_as_the_server_named_it";

    private const string PostgresSql = "SELECT c.\"Id\", c.\"SSN\" AS kt FROM public.\"Customer\" c";
    private const string SqlServerSql = "SELECT c.Id, c.SSN AS kt FROM dbo.Customer c";

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector();
    }

    // --- the provider's read paths ------------------------------------------------------------------------------------

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task DatabaseProvider_MasksTheMaskedPosition_WhateverTheDatabaseNamesIt(bool npgsql, bool readOnly)
    {
        Connect(npgsql, ServerColumns());
        var provider = Provider(npgsql);

        var result = readOnly
            ? await provider.ExecuteReadOnlyQueryAsync(HostSource(npgsql), Sql(npgsql), [], CancellationToken.None)
            : await provider.ExecuteQueryAsync(HostSource(npgsql), Sql(npgsql), [], CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().OnlyContain(x => (string?)x[ServerLabel] == HostResultMasker.MaskedValue);
        result.Rows.Select(x => x["Id"]).Should().Equal(1, 2);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DatabaseProvider_RowCappedRead_MasksTheMaskedPosition(bool npgsql)
    {
        Connect(npgsql, ServerColumns(), rowCount: 5);
        var provider = (IRowCappedReadOnlyExecution)Provider(npgsql);

        var result = await provider.ExecuteReadOnlyQueryAsync(HostSource(npgsql), Sql(npgsql), [], 3, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(3);
        result.Rows.Should().OnlyContain(x => (string?)x[ServerLabel] == HostResultMasker.MaskedValue);
    }

    // The masked column comes last under the name of an unmasked one: keyed by name the later column wins, so its value is
    // masked by position before the rows are keyed. The Dapper path (SQL Server) reads a repeated name as two columns.
    [TestCase(true, "SELECT c.\"Name\" AS x, c.\"SSN\" AS x FROM public.\"Customer\" c")]
    [TestCase(false, "SELECT c.Name AS x, c.SSN AS x FROM dbo.Customer c")]
    public async Task DatabaseProvider_ARepeatedName_DoesNotUnmaskThePosition(bool npgsql, string sql)
    {
        Connect(npgsql, [("x", _ => "Anna"), ("x", _ => Ssn)]);

        var result = await Provider(npgsql).ExecuteReadOnlyQueryAsync(HostSource(npgsql), sql, [], CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().OnlyContain(x => (string?)x["x"] == HostResultMasker.MaskedValue);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DatabaseProvider_AResultWithAnotherColumnCount_IsDiscarded_AndLoggedByCountsOnly(bool npgsql)
    {
        Connect(npgsql, [("Id", x => x + 1), ("kt", _ => Ssn), ("extra", _ => Ssn)]);
        var log = new LogRecorder();

        var result = await Provider(npgsql, log.For<DatabaseProvider>()).ExecuteReadOnlyQueryAsync(HostSource(npgsql), Sql(npgsql), [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(DatabaseProvider.HostResultMismatchMessage);
        result.Rows.Should().BeEmpty();
        log.Entries.Should().ContainSingle()
            .Which.Message.Should().Be($"A result from host data source {HostDataSourceId} had 3 columns where the host policy checked 2; it was discarded");
        log.Contains(Ssn).Should().BeFalse();
        log.Contains("kt").Should().BeFalse("column names are caller-chosen query text");
    }

    // The count of columns masked for their name travels with the result, so a caller can say why they came back masked.
    [Test]
    public async Task DatabaseProvider_AColumnNamedOtherwise_IsLoggedByCountsOnly_AndCountedOnTheResult()
    {
        Connect(true, ServerColumns());
        var log = new LogRecorder();

        var result = await Provider(true, log.For<DatabaseProvider>()).ExecuteReadOnlyQueryAsync(HostSource(true), PostgresSql, [], CancellationToken.None);

        result.Metadata.Should().Contain(HostResultMetadata.RenamedMaskedColumns, 1);
        log.Entries.Should().ContainSingle()
            .Which.Message.Should().Be($"1 of 2 result columns from host data source {HostDataSourceId} were not named as the host policy derived them and were masked");
        log.Contains(ServerLabel).Should().BeFalse();
        log.Contains(Ssn).Should().BeFalse();
    }

    [Test]
    public async Task DatabaseProvider_AnOrdinarySource_IsNotMasked()
    {
        Connect(true, ServerColumns());
        var ordinary = HostSource(true);
        ordinary.HostManagedKey = null;

        var result = await Provider(true).ExecuteReadOnlyQueryAsync(ordinary, PostgresSql, [], CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().OnlyContain(x => (string?)x[ServerLabel] == Ssn);
    }

    // --- previews -----------------------------------------------------------------------------------------------------

    [Test]
    public async Task PagedStepPreview_MasksTheMaskedPosition_OnThePageAndCountRewrites()
    {
        var connection = Connect(true, ServerColumns(), columnsFor: x => x.Contains("COUNT(*)") ? [("count", _ => 2L)] : null);

        var result = await PreviewService().PreviewSnapshotStep(Snapshot(null), 1, null, new Paging { Page = 0, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.BatchedCommands.Should().Contain(x => x.Contains("LIMIT 20 OFFSET 0"), "the page rewrite ran");
        result.Result!.TotalCount.Should().Be(2);
        result.Result.Rows.Should().HaveCount(2)
            .And.OnlyContain(x => (string?)x[ServerLabel] == HostResultMasker.MaskedValue);
    }

    [Test]
    public async Task QueryPreview_TheFinalQueryOnTheJoin_ReadsTheMaskedValue()
    {
        Connect(true, ServerColumns());

        var result = await PreviewService().PreviewSnapshot(
            Snapshot($"SELECT r.Id, r.{ServerLabel} AS copy FROM @result1 r ORDER BY r.Id"),
            new Paging { Page = 0, PageSize = 20 },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Steps.Single().PreviewRows.Should().OnlyContain(x => (string?)x[ServerLabel] == HostResultMasker.MaskedValue);
        result.Result!.Rows.Should().HaveCount(2)
            .And.OnlyContain(x => ((string)x["copy"]!).All(y => y == '*'));
    }

    // --- the MCP query tool -------------------------------------------------------------------------------------------

    [Test]
    public async Task McpQueryTool_MasksTheMaskedPosition()
    {
        Connect(true, ServerColumns());

        var result = await TestToolCalls.InvokeAsync(QueryTool(), x => x.ExecuteAsync(datasource_id: HostDataSourceId, sql: PostgresSql, cancellationToken: CancellationToken.None));

        (result.IsError ?? false).Should().BeFalse();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Split('\n').Where(x => x.StartsWith("| ", StringComparison.Ordinal)).Skip(2).Should().Equal("| 1 | *** |", "| 2 | *** |");
        text.Should().NotContain(Ssn);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("columns").EnumerateArray().Select(x => x.GetString()).Should().Equal("Id", ServerLabel);
        structured.GetProperty("rows").EnumerateArray()
            .Select(x => (x[0].GetInt32(), x[1].GetString()))
            .Should().Equal((1, HostResultMasker.MaskedValue), (2, HostResultMasker.MaskedValue));
    }

    // A provider that returns the host-masked value as it is: the tool masks it in full from the gate's report, whatever
    // the detection setting, and leaves the other column alone.
    [TestCase(true)]
    [TestCase(false)]
    public async Task McpQueryTool_ARawHostMaskedValue_IsMaskedInFull(bool piiDetection)
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["Id"] = 1, ["kt"] = Ssn }]
            });

        var result = await TestToolCalls.InvokeAsync(QueryTool(provider.Object, piiDetection), x => x.ExecuteAsync(datasource_id: HostDataSourceId, sql: PostgresSql, cancellationToken: CancellationToken.None));

        (result.IsError ?? false).Should().BeFalse();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().NotContain(Ssn);
        result.StructuredContent!.Value.GetProperty("rows").EnumerateArray()
            .Select(x => (x[0].GetInt32(), x[1].GetString()))
            .Should().Equal((1, HostResultMasker.MaskedValue));
    }

    // With PII detection off the project brief still states that the host's masked columns are masked, because the query
    // tool masks them from the host policy whatever the detection setting.
    [Test]
    public async Task ProjectBrief_PiiDetectionOff_StatesTheMaskingTheQueryToolApplies()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "SSN"), npgsql: true);
        var maskedColumns = snapshot.Tables
            .SelectMany(x => x.Columns
                .Where(y => y.Description != null)
                .Where(y => y.Description!.Contains(HostModelReader.MaskedDescriptionMarker))
                .Select(y => new BriefMaskedColumn(x.SchemaName, x.TableName, y.ColumnName)))
            .ToList();
        var brief = ProjectBriefService.Render(new ProjectBriefData(
            "Netgiro",
            null,
            [new BriefDataSource("Netgiro", DataSourceType.Database, DatabaseEngineType.PostgreSQL, false, true, 1)],
            [],
            0,
            maskedColumns,
            [],
            0,
            [],
            0,
            Guarantees: new BriefGuarantees(ReadOnlyEnforced: true, PiiDetection: false)));
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["Id"] = 1, ["kt"] = Ssn }]
            });

        var result = await QueryTool(provider.Object, piiDetection: false).ExecuteAsync(datasource_id: HostDataSourceId, sql: PostgresSql, cancellationToken: CancellationToken.None);

        maskedColumns.Should().ContainSingle().Which.ColumnName.Should().Be("SSN");
        brief.Should().Contain("Values of these columns are masked in every query result by the host application's policy");
        brief.Should().Contain("- `public.Customer.SSN`");
        result.StructuredContent!.Value.GetProperty("rows").EnumerateArray()
            .Select(x => x[1].GetString())
            .Should().Equal(HostResultMasker.MaskedValue);
    }

    private static IReadOnlyList<(string Name, Func<int, object?> Value)> ServerColumns() => [("Id", x => x + 1), (ServerLabel, _ => Ssn)];

    private static string Sql(bool npgsql) => npgsql ? PostgresSql : SqlServerSql;

    private static RecordingDbConnection Connect(
        bool npgsql,
        IReadOnlyList<(string Name, Func<int, object?> Value)> columns,
        int rowCount = 2,
        Func<string, IReadOnlyList<(string Name, Func<int, object?> Value)>?>? columnsFor = null)
    {
        var connection = new RecordingDbConnection { Columns = columns, RowCount = rowCount, ColumnsFor = columnsFor };
        DbConnectionFactory.Register(HostTestModelFactory.EngineOf(npgsql), _ => connection);

        return connection;
    }

    private static HostDataSourceGuard Guard(bool npgsql)
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "SSN"), npgsql);

        return new HostDataSourceGuard(new SingleHostRegistry(snapshot));
    }

    private static DatabaseProvider Provider(bool npgsql, ILogger<DatabaseProvider>? logger = null)
    {
        var connections = TestQueryServices.Connections();

        return new DatabaseProvider(
            connections,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            Guard(npgsql),
            TestConfiguredDatabases.None,
            logger ?? NullLogger<DatabaseProvider>.Instance,
            TestQueryServices.QueryOptions);
    }

    private static DataSource HostSource(bool npgsql)
    {
        return new DataSource
        {
            Id = HostDataSourceId,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = HostTestModelFactory.EngineOf(npgsql),
            HostManagedKey = HostKey,
            EncryptedConnectionData = "reference"
        };
    }

    private static QueryService PreviewService()
    {
        // PII detection off: what is masked here is masked by the host policy alone.
        return TestQueryServices.QueryService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            hostGuard: Guard(npgsql: true),
            sharedSourceSettings: TestQueryServices.DefaultSettings(new McpSettingsData { EnablePiiDetection = false }));
    }

    private static StoredQuerySnapshot Snapshot(string? finalQuery)
    {
        var dataSource = HostSource(npgsql: true);

        return new StoredQuerySnapshot
        {
            QueryId = 1,
            Steps =
            [
                new QueryStep
                {
                    QueryId = 1,
                    StepOrder = 1,
                    DataSourceId = dataSource.Id,
                    DataSource = dataSource,
                    SqlValue = PostgresSql
                }
            ],
            FinalQuery = finalQuery
        };
    }

    private static ProjectQueryTool QueryTool(IDataSourceProvider? provider = null, bool piiDetection = false)
    {
        var guard = Guard(npgsql: true);
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet([HostSource(npgsql: true)], saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet([new ProjectDataSource { ProjectId = ProjectId, DataSourceId = HostDataSourceId }], saved)
                },
                saved));

        var settings = new McpSettingsData { EnablePiiDetection = piiDetection };
        var settingsProvider = SettingsProviderMock.Create(settings);
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(It.IsAny<int>(), ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        // The audit and signal services swallow sink failures by design (§1.7/§9.5), so bare factory mocks suffice.
        var auditService = new McpAuditService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            SettingsProviderMock.Create().Object,
            new HttpContextAccessor(),
            Options.Create(new McpDeploymentOptions()),
            NullLogger<McpAuditService>.Instance,
            new McpAuditOutcome(),
            Options.Create(new BeaconTelemetryOptions()),
            NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog());
        var signalService = new McpSignalService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            settingsProvider.Object,
            NullLogger<McpSignalService>.Instance);
        var guardrail = new QueryGuardrailService();

        return TestToolCalls.Track(recorder => new ProjectQueryTool(
            factory.Object,
            TestQueryServices.Providers(provider ?? Provider(npgsql: true)),
            guardrail,
            TestSqlGate.Create(guardrail, guard),
            TestConfiguredDatabases.None,
            TestRowSecurity.Resolver,
            knowledgeGraph.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            recorder,
            new McpQueryConcurrencyLimiter(),
            TestQueryCostLimit.Passing,
            NullLogger<ProjectQueryTool>.Instance), () => TestToolCalls.Recorder(auditService, signalService));
    }

    private sealed record Paging : ListRequest;

    private sealed class SingleHostRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == HostKey ? snapshot : null;
    }
}
