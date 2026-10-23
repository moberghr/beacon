using System.Collections;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    [Test]
    public async Task EachDiscoveryStatement_IsInTheExecutionLogBeforeItRuns_AndClosedWhenItEnds()
    {
        var order = new List<string>();
        var audit = new RecordingExecutionAudit(order);
        var provider = RecordingProvider();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("execute"))
            .ReturnsAsync(new ProviderQueryResult { Success = true });
        var service = Service(provider.Object, [Table("orders", "status")], executionAudit: audit);

        await GenerateIgnoringLaterFailures(service);

        order.Should().Equal("start", "execute", "end");
        audit.Starts.Should().ContainSingle()
            .Which.Should().Match<SqlExecutionStart>(x => x.DataSourceId == DataSourceId
                && x.ExecutionContext == SqlExecutionContexts.DocumentationEnumDiscovery
                && x.Sql.Contains("\"orders\""));
        audit.Ends.Should().ContainSingle().Which.Status.Should().Be(QueryExecutionLogStatus.Succeeded);
    }

    [Test]
    public async Task ASmallProjectsPrompts_AreUnchangedByThePromptBudget()
    {
        var prompts = new List<string>();
        var service = Service(RecordingProvider().Object, [Table("public", "orders", "id", "status"), Table("public", "events", "id")], prompts: prompts);

        await GenerateIgnoringLaterFailures(service);

        var nl = Environment.NewLine;
        var dataModel = prompts.Should().ContainSingle(x => x.StartsWith("# Full Schema Metadata")).Subject;
        dataModel.Should().Be(string.Join(nl, "# Full Schema Metadata", "", "## Data Source: core", "### public.events", "Columns:", "  - id integer", "",
            "### public.orders", "Columns:", "  - id integer", "  - status integer", "", ""));
        var domains = prompts.Should().ContainSingle(x => x.StartsWith("# Tables and Relationships")).Subject;
        domains.Should().Be(string.Join(nl, "# Tables and Relationships", "", "## Data Source: core", "### public.events", "  Columns: id", "### public.orders",
            "  Columns: id, status", "", "# Code References by Class", ""));
        prompts.Should().NotContain(x => x.Contains("not shown"));
    }

    [Test]
    public async Task AProjectBeyondThePromptBudget_GetsPromptsCutAtATableBoundary_WithTheCountOfTheRest()
    {
        const int tableCount = 800;
        var tables = Enumerable.Range(0, tableCount)
            .Select(i => Table("public", $"table_{i:D4}", Enumerable.Range(0, 12).Select(c => $"column_{c}_of_table_{i:D4}_with_a_long_name").ToArray()))
            .ToList();
        var prompts = new List<string>();
        var service = Service(RecordingProvider().Object, tables, prompts: prompts);

        await GenerateIgnoringLaterFailures(service);

        var dataModel = prompts.Should().ContainSingle(x => x.StartsWith("# Full Schema Metadata")).Subject;
        var shown = dataModel.Split("### public.").Length - 1;
        shown.Should().BeInRange(1, tableCount - 1);
        var note = $"+{tableCount - shown} more not shown (prompt size limit)";
        dataModel.TrimEnd().Should().EndWith(note);
        dataModel.Length.Should().BeLessThanOrEqualTo(DocumentationPromptBudget.DefaultLimit);
        // The cut falls between tables: the last shown table is whole, in its order.
        dataModel.Should().Contain($"### public.table_{shown - 1:D4}");
        dataModel.Should().NotContain($"### public.table_{shown:D4}");
        dataModel.Should().Contain($"column_11_of_table_{shown - 1:D4}_with_a_long_name");
        prompts.Where(x => x.StartsWith("# Tables and Relationships")).Single().Length.Should().BeLessThanOrEqualTo(DocumentationPromptBudget.DefaultLimit);
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
            sql, engine.ToString(), true, false, null, null, false, null, false, null, TestRowSecurity.NotApplicable)
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

    // Discovered values are row data for the documentation prompts: none is read while sample-value collection is off for
    // the project and the projects that share the source (the default, and what the ForbidRowDataToLlm lock pins).
    [Test]
    public async Task WhileSampleValueCollectionIsOff_NoColumnValueIsDiscovered_AndTheRunSaysSoOnce()
    {
        var provider = RecordingProvider();
        var log = new LogRecorder();
        var service = Service(provider.Object, [Table("public", "orders", "status")], settings: new McpSettingsData(), logger: log.For<ProjectDocumentationService>());

        await GenerateIgnoringLaterFailures(service);

        provider.Invocations
            .Where(x => x.Method.Name is nameof(IDataSourceProvider.ExecuteQueryAsync) or nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync))
            .Should().BeEmpty();
        log.Entries
            .Where(x => x.Message.StartsWith("Enum value discovery for project", StringComparison.Ordinal))
            .Should().ContainSingle()
            .Which.Should().Match<RecordedLog>(x => x.Level == LogLevel.Information
                && x.Message == $"Enum value discovery for project {ProjectId} lists no values of data sources {DataSourceId}: sample-value collection is off for them");
    }

    [Test]
    public async Task WhileSampleValueCollectionIsOn_TheRunDoesNotSayItIsOff()
    {
        var log = new LogRecorder();
        var service = Service(RecordingProvider().Object, [Table("public", "orders", "status")], logger: log.For<ProjectDocumentationService>());

        await GenerateIgnoringLaterFailures(service);

        log.Contains("sample-value collection is off").Should().BeFalse();
    }

    // A PII name (a banking identifier included), a secret-like name or a financial amount is never read; the other
    // candidates still are.
    [Test]
    public async Task AnExcludedColumnName_IsNeverRead()
    {
        var provider = RecordingProvider();
        var service = Service(
            provider.Object,
            [Table("public", "accounts", "iban", "status", "pin_retry_count", "balance_band", "customer_oib")],
            settings: new McpSettingsData { EnableSampleValueCollection = true, CustomPiiPatterns = ["^status$"] });

        await GenerateIgnoringLaterFailures(service);

        provider.Invocations
            .Where(x => x.Method.Name == nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync))
            .Should().BeEmpty("every candidate column of the table is excluded, the custom pattern's included");
    }

    // A column one of whose values looks like PII or a credential lists none of its values; another column keeps its own.
    [Test]
    public async Task AColumnWithAPiiOrCredentialShapedValue_ListsNone()
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
                    Row(1, "HR1210010051863000160", 2),
                    Row(2, "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8", 1)
                ]
            });
        var prompts = new List<string>();
        var service = Service(provider.Object, [Table("public", "orders", "status", "kind", "region")], prompts: prompts);

        await GenerateIgnoringLaterFailures(service);

        var described = string.Join("\n", prompts);
        described.Should().Contain("status values: [open]");
        described.Should().NotContain("HR1210010051863000160").And.NotContain("ghp_");
    }

    // Every prompt that lists discovered values is logged as row data sent to the LLM provider: the project, the data
    // source, the category and the user who asked for the documentation — never a value.
    [Test]
    public async Task APromptListingDiscoveredValues_IsLoggedWithIdsAndCategoryOnly()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [Row(0, "SECRETVALUE-open", 5)] });
        var log = new LogRecorder();
        var prompts = new List<string>();
        var service = Service(provider.Object, [Table("public", "orders", "status")], prompts: prompts, egress: TestRowDataEgress.Logging(log.For<LlmRowDataEgress>()));

        await GenerateIgnoringLaterFailures(service);

        string.Join("\n", prompts).Should().Contain("SECRETVALUE-open", "the value reached the prompt, so its request is logged");
        var egress = log.Entries
            .Where(x => x.EventId == Beacon.Core.Authorization.ProjectAccess.ProjectAccessEvents.RowDataSentToLlm)
            .ToList();
        egress.Should().NotBeEmpty();
        egress.Where(x => x.Level == LogLevel.Information).Should().NotBeEmpty()
            .And.OnlyContain(x => x.Message == $"An LLM request carrying {LlmRowDataCategories.ColumnValues} read from data sources {DataSourceId} of project {ProjectId} was handed to the provider for caller 1");
        egress.Where(x => x.Level != LogLevel.Information).Should().OnlyContain(x => x.Level == LogLevel.Warning && x.Message.Contains("failed at the provider (InvalidOperationException", StringComparison.Ordinal), "the model in this test fails every request");
        log.Contains("SECRETVALUE").Should().BeFalse("a value never reaches the log");
    }

    // Only the passes that are handed the discovered values — business domains, data model and the glossary — send them,
    // so only their requests are logged; the overview, data flow, code lineage and data quality passes are not.
    [Test]
    public async Task OnlyThePassesHandedDiscoveredValues_AreLogged()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [Row(0, "open", 5)] });
        var egress = new RecordingEgress();
        var service = Service(provider.Object, [Table("public", "orders", "status")], egress: egress);

        await GenerateIgnoringLaterFailures(service);

        egress.TrackedSystemPrompts.Select(PassOf).Should().BeEquivalentTo(["business domains", "data model", "glossary"]);
        egress.Scopes.Should().OnlyContain(x => x.Categories.SequenceEqual(new[] { LlmRowDataCategories.ColumnValues }));
    }

    private static string PassOf(string systemPrompt)
    {
        return systemPrompt switch
        {
            _ when systemPrompt.Contains("logical business domains", StringComparison.Ordinal) => "business domains",
            _ when systemPrompt.Contains("data model documentation", StringComparison.Ordinal) => "data model",
            _ when systemPrompt.Contains("glossary", StringComparison.Ordinal) => "glossary",
            _ when systemPrompt.Contains("project overview", StringComparison.Ordinal) => "overview",
            _ when systemPrompt.Contains("data flow", StringComparison.Ordinal) => "data flows",
            _ when systemPrompt.Contains("lineage", StringComparison.Ordinal) => "code lineage",
            _ when systemPrompt.Contains("data quality", StringComparison.Ordinal) => "data quality",
            _ => "other"
        };
    }

    // Records each prompt that goes through a tracked provider (one with categories), then sends it on.
    private sealed class RecordingEgress : ILlmRowDataEgress
    {
        public List<LlmRowDataScope> Scopes { get; } = [];

        public List<string> TrackedSystemPrompts { get; } = [];

        public ILlmProvider Track(ILlmProvider provider, LlmRowDataScope scope)
        {
            if (scope.Categories.Count == 0)
            {
                return provider;
            }

            Scopes.Add(scope);

            return new Recording(provider, this);
        }

        public Task RecordReturnedToMcpClientAsync(LlmRowDataScope scope, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        private sealed class Recording(ILlmProvider inner, RecordingEgress egress) : ILlmProvider
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                lock (egress.TrackedSystemPrompts)
                {
                    egress.TrackedSystemPrompts.Add(request.SystemPrompt ?? "");
                }

                return inner.CompleteAsync(request, cancellationToken);
            }

            public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
            {
                return inner.CountTokensAsync(text, cancellationToken);
            }
        }
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
        List<string>? prompts = null,
        McpSettingsData? settings = null,
        ILlmRowDataEgress? egress = null,
        ILogger<ProjectDocumentationService>? logger = null,
        ISqlExecutionAudit? executionAudit = null)
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

        var shared = new Mock<ISharedSourceSettings>();
        shared
            .Setup(x => x.GetEffectiveSettingsAsync(ProjectId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new McpSettingsData { EnableSampleValueCollection = true });

        return new ProjectDocumentationService(
            factory.Object,
            llm.Object,
            providers.Object,
            TestSqlGate.Create(),
            TestConfiguredDatabases.Of(configuredDatabase),
            TestRowSecurity.Resolver,
            shared.Object,
            new QueryGuardrailService(),
            egress ?? TestRowDataEgress.Discarding(),
            logger ?? NullLogger<ProjectDocumentationService>.Instance,
            executionAudit);
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
