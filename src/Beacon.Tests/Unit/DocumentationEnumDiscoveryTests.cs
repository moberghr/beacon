using System.Collections;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Project documentation samples the distinct values of short columns ("enum discovery") with SQL Beacon builds from the
/// stored catalog. The catalog copies schema, table and column names from the connected database, so those names are
/// quoted in each engine's own way, the column is identified by its position rather than by a literal of its name, a name
/// carrying quote, bracket, semicolon or control characters is skipped, and the statement passes the read-only gate and
/// runs through the provider's read-only path. No database: in-memory sets and a recording provider (§4.7).
/// </summary>
[TestFixture]
public class DocumentationEnumDiscoveryTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = 7;
    private const string ColumnCarryingSql = "status';SELECT pg_sleep(30);--";

    [Test]
    public async Task ACatalogNameCarryingSql_NeverReachesTheDataSource_AndDiscoveryRunsReadOnly()
    {
        var provider = RecordingProvider();
        var service = Service(provider.Object, [Table("events", ColumnCarryingSql), Table("orders", "status")]);

        await GenerateIgnoringLaterFailures(service);

        var statements = provider.Invocations
            .Where(x => x.Method.Name is nameof(IDataSourceProvider.ExecuteQueryAsync) or nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync))
            .Select(x => (Method: x.Method.Name, Sql: (string)x.Arguments[1]))
            .ToList();
        statements.Should().NotContain(x => x.Sql.Contains("pg_sleep", StringComparison.OrdinalIgnoreCase));
        statements.Should().NotContain(x => x.Method == nameof(IDataSourceProvider.ExecuteQueryAsync), "discovery runs on the read-only path only");
        statements.Should().ContainSingle(x => x.Sql.Contains("\"orders\"") && x.Sql.Contains("\"status\""));
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "\"we\"\"ird\"", "\"pub\"\"lic\".\"ord\"\"ers\"", "TEXT")]
    [TestCase(DatabaseEngineType.Snowflake, "\"we\"\"ird\"", "\"pub\"\"lic\".\"ord\"\"ers\"", "TEXT")]
    [TestCase(DatabaseEngineType.MSSQL, "[we\"ird]", "[pub\"lic].[ord\"ers]", "VARCHAR(MAX)")]
    [TestCase(DatabaseEngineType.AzureSynapse, "[we\"ird]", "[pub\"lic].[ord\"ers]", "VARCHAR(MAX)")]
    [TestCase(DatabaseEngineType.MySQL, "`we\"ird`", "`pub\"lic`.`ord\"ers`", "CHAR")]
    public void TheDiscoveryStatement_QuotesNamesTheEnginesOwnWay(DatabaseEngineType engine, string column, string table, string castType)
    {
        var sql = ProjectDocumentationService.BuildEnumDiscoveryQuery(engine, "pub\"lic", "ord\"ers", ["we\"ird"]);

        sql.Should().Contain($"CAST({column} AS {castType}) AS val");
        sql.Should().Contain($"FROM {table} GROUP BY {column}");
        sql.Should().NotContain("'", "the column is identified by its position, never by a literal of its name");
    }

    [TestCase(DatabaseEngineType.MSSQL, "[a]]b]")]
    [TestCase(DatabaseEngineType.MySQL, "`a``b`")]
    [TestCase(DatabaseEngineType.PostgreSQL, "\"a\"\"b\"")]
    public void TheEnginesOwnQuoteCharacter_IsDoubled(DatabaseEngineType engine, string quoted)
    {
        var name = engine switch
        {
            DatabaseEngineType.MSSQL => "a]b",
            DatabaseEngineType.MySQL => "a`b",
            _ => "a\"b"
        };

        ProjectDocumentationService.BuildEnumDiscoveryQuery(engine, "s", "t", [name])
            .Should().Contain($"GROUP BY {quoted}");
    }

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.Snowflake)]
    public void SeveralColumns_AreOneReadOnlyStatement_TheGateAccepts(DatabaseEngineType engine)
    {
        var sql = ProjectDocumentationService.BuildEnumDiscoveryQuery(engine, "sales", "orders", ["status", "kind", "region"]);

        var report = TestSqlGate.Create().Evaluate(new Beacon.Core.Services.Validation.SqlGateRequest(
            sql, engine.ToString(), true, false, null, null, false, null, false, null)
        { ConfiguredDatabase = "sales" });

        report.Blocked.Should().BeFalse(report.BlockReason);
        sql.Should().Contain("SELECT * FROM (").And.Contain(" UNION ALL ").And.Contain("2 AS col");
    }

    // Each result row names its column by position: the values land on the column at that position, a marker that names no
    // candidate is dropped, and a value is never attributed to another column.
    [Test]
    public async Task EachValue_IsAttributedToTheColumnItsPositionNames()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows =
                [
                    Row(0, "open", 5),
                    Row(1, "retail", 2),
                    Row(0, "closed", 3),
                    Row(7, "stray", 9),
                    Row("x", "unmarked", 9)
                ]
            });
        var prompts = new List<string>();
        var service = Service(provider.Object, [Table("public", "orders", "status", "kind")], prompts: prompts);

        await GenerateIgnoringLaterFailures(service);

        var described = string.Join("\n", prompts);
        described.Should().Contain("status values: [open, closed]").And.Contain("kind values: [retail]");
        described.Should().NotContain("stray").And.NotContain("unmarked");
    }

    // A schema or table name carrying a quote, bracket, semicolon or control character is never built into a statement.
    [TestCase("sa\"les", "orders")]
    [TestCase("sales", "ord;ers")]
    [TestCase("sales", "ord`ers")]
    [TestCase("sa]les", "orders")]
    [TestCase("sales", "ord\ners")]
    public async Task ATableWhoseNameCarriesAQuoteBracketSemicolonOrControlCharacter_IsSkipped(string schema, string table)
    {
        var provider = RecordingProvider();
        var service = Service(provider.Object, [Table(schema, table, "status"), Table("sales", "invoices", "status")]);

        await GenerateIgnoringLaterFailures(service);

        var statements = provider.Invocations
            .Where(x => x.Method.Name == nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync))
            .Select(x => (string)x.Arguments[1])
            .ToList();
        statements.Should().ContainSingle().Which.Should().Contain("\"invoices\"");
    }

    // On every engine the statement passes the gate in the source's dialect, with its own quoting, and runs read-only.
    [TestCase(DatabaseEngineType.MySQL, "`sales`.`orders`", "`status`")]
    [TestCase(DatabaseEngineType.MSSQL, "[sales].[orders]", "[status]")]
    [TestCase(DatabaseEngineType.Snowflake, "\"sales\".\"orders\"", "\"status\"")]
    public async Task OnEveryEngine_DiscoveryRunsReadOnly_WithTheEnginesQuoting(DatabaseEngineType engine, string table, string column)
    {
        var provider = RecordingProvider();
        var service = Service(provider.Object, [Table("sales", "orders", "status")], engine, configuredDatabase: "sales");

        await GenerateIgnoringLaterFailures(service);

        var statements = provider.Invocations
            .Where(x => x.Method.Name is nameof(IDataSourceProvider.ExecuteQueryAsync) or nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync))
            .Select(x => (Method: x.Method.Name, Sql: (string)x.Arguments[1]))
            .ToList();
        statements.Should().ContainSingle().Which.Method.Should().Be(nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync));
        statements.Single().Sql.Should().Contain($"FROM {table} GROUP BY {column}");
    }

    private static Dictionary<string, object?> Row(object marker, string value, long count)
    {
        return new Dictionary<string, object?> { ["col"] = marker, ["val"] = value, ["cnt"] = count };
    }

    private static async Task GenerateIgnoringLaterFailures(ProjectDocumentationService service)
    {
        try
        {
            await service.GenerateDocumentationAsync(ProjectId, 1, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            // Only the discovery statements matter here; the language model and persistence are stubs.
            TestContext.Out.WriteLine($"generation ended with {ex.GetType().Name}");
        }
    }

    private static Mock<IDataSourceProvider> RecordingProvider()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true });
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true });

        return provider;
    }

    private static ProjectDocumentationService Service(
        IDataSourceProvider provider,
        List<DatabaseMetadata> tables,
        DatabaseEngineType engine = DatabaseEngineType.PostgreSQL,
        string? configuredDatabase = null,
        List<string>? prompts = null)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "core",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "encrypted"
        };
        var project = new Project { Id = ProjectId, Name = "p" };
        var link = new ProjectDataSource { Id = 1, ProjectId = ProjectId, Project = project, DataSourceId = DataSourceId, DataSource = dataSource };
        foreach (var table in tables)
        {
            table.DataSource = dataSource;
        }

        var sets = new Dictionary<Type, IList>
        {
            [typeof(DataSource)] = new List<DataSource> { dataSource },
            [typeof(Project)] = new List<Project> { project },
            [typeof(ProjectDataSource)] = new List<ProjectDataSource> { link },
            [typeof(DatabaseMetadata)] = tables
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ServedContext(sets));
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider);
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((x, _) => prompts?.Add(string.Join("\n", x.Messages.Select(y => y.Content))))
            .ThrowsAsync(new InvalidOperationException("no model in this test"));

        return new ProjectDocumentationService(
            factory.Object,
            Mock.Of<IKnowledgeGraphService>(),
            llm.Object,
            providers.Object,
            TestSqlGate.Create(),
            TestConfiguredDatabases.Of(configuredDatabase),
            NullLogger<ProjectDocumentationService>.Instance);
    }

    private static DatabaseMetadata Table(string name, string column)
    {
        return Table("public", name, column);
    }

    private static DatabaseMetadata Table(string schema, string name, params string[] columns)
    {
        var table = new DatabaseMetadata { Id = (schema + name).GetHashCode() & 0x7fffffff, DataSourceId = DataSourceId, SchemaName = schema, TableName = name };
        table.Columns = columns
            .Select((x, i) => new ColumnMetadata { ColumnName = x, DataType = "integer", IsNullable = true, OrdinalPosition = i + 1, DatabaseMetadata = table })
            .ToList();

        return table;
    }

    /// <summary>Serves the given sets from memory; every other set is empty. Saving records nothing.</summary>
    private sealed class ServedContext(Dictionary<Type, IList> sets) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<ServedContext> Options =
            new DbContextOptionsBuilder<ServedContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>()
        {
            var rows = sets.TryGetValue(typeof(TEntity), out var list)
                ? list.Cast<TEntity>().ToList()
                : new List<TEntity>();

            return RecordingBeaconContext.MemorySet(rows, []);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
