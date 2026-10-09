using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using ProviderValidationResult = Beacon.Core.Models.Providers.QueryValidationResult;

namespace Beacon.Tests.Unit;

/// <summary>
/// The cross-source <c>ask</c> path's gate wiring (spec <c>sql-execution-gate</c>, caller-mapping rows for
/// <c>CrossSourceQueryService</c>): loop 1 acts on the schema verdict only; loop 2 runs the read-only gate
/// BEFORE the provider dry-run (a write must never reach EXPLAIN), adopts a dry-run repair only when it clears
/// the gate, and executes the gate's <c>FinalSql</c> capped by <c>settings.MaxRowLimit</c>; the LLM join SQL
/// is gated too. Real gate over the real validators and guardrail; provider, knowledge graph, SQL generation
/// and LLM are mocked; the in-memory SQLite join store is real (§4.7 — no Beacon DB).
/// </summary>
[TestFixture]
public class CrossSourceQueryServiceTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;
    private const int CrmDataSourceId = 8;
    private const int MaxRowLimit = 250;
    private const string WarehouseContext = "schema context";
    private const string CrmContext = "crm schema context";
    private const string HostKey = "efcore:Warehouse";

    private Mock<IDataSourceProvider> _provider = null!;
    private Mock<ISqlGenerationService> _sqlGen = null!;
    private Mock<ILlmProvider> _llm = null!;
    private McpSignalBuilder _signal = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new Mock<IDataSourceProvider>();
        _provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderValidationResult { IsValid = true });
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["id"] = 1 }]
            });

        _sqlGen = new Mock<ISqlGenerationService>();
        _llm = new Mock<ILlmProvider>();
        _llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "SELECT * FROM result1" });

        _signal = new McpSignalBuilder().SetTool("ask").SetQuestion("q");
    }

    [Test]
    public async Task WriteStatement_IsBlockedBeforeProviderDryRun_AndNeverExecuted()
    {
        Generates("DELETE FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeFalse();
        text.Should().Contain("Validation Error for warehouse");
        _provider.Verify(
            x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a write statement must never reach the provider dry-run (EXPLAIN)");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ExecutedSql_IsCappedBySettingsMaxRowLimit_NotAConstant()
    {
        Generates("SELECT id FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                $"SELECT id FROM orders LIMIT {MaxRowLimit}",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the per-source cap comes from the operator's MaxRowLimit, applied to the SQL that actually runs");
        text.Should().Contain("### Final Results");
    }

    [Test]
    public async Task SchemaFailure_RetryThatClearsTheGate_IsAdoptedAndExecuted()
    {
        Generates("SELECT bogus FROM orders");
        _sqlGen
            .Setup(x => x.RetryWithErrorAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), "SELECT bogus FROM orders", It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SELECT id FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.Is<string>(s => s.StartsWith("SELECT id FROM orders")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _signal.Build().SchemaValidationFailed.Should().BeTrue();
        _signal.Build().RetrySucceeded.Should().BeTrue();
    }

    [Test]
    public async Task SchemaFailure_RetryThatStillFails_ExcludesTheSource()
    {
        Generates("SELECT bogus FROM orders");
        _sqlGen
            .Setup(x => x.RetryWithErrorAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SELECT still_bogus FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeFalse();
        text.Should().Contain("Schema Validation Error");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task DryRunRepair_RetryThatFailsReadOnly_IsNotAdopted()
    {
        Generates("SELECT id FROM orders");
        _provider
            .SetupSequence(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderValidationResult { IsValid = false, Errors = ["relation missing"] })
            .ReturnsAsync(new ProviderValidationResult { IsValid = true });
        _sqlGen
            .Setup(x => x.RetryWithErrorAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("DELETE FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.Is<string>(s => s.StartsWith("SELECT id FROM orders")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a repair that fails the read-only gate is dropped and the original SQL runs");
    }

    [Test]
    public async Task JoinSql_ThatFailsReadOnly_IsRefused()
    {
        Generates("SELECT id FROM orders");
        _llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "DELETE FROM result1" });

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeFalse();
        text.Should().Contain("Validation Error for join query");
    }

    [Test]
    public async Task PreviewMode_RendersSqlWithoutExecuting()
    {
        Generates("SELECT id FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: false, _signal, CancellationToken.None);

        succeeded.Should().BeTrue();
        text.Should().Contain("Execution skipped");
        _provider.VerifyNoOtherCalls();
    }

    [TestCase("SELECT * FROM orders", "email")]
    [TestCase("SELECT * FROM orders", "customer_email")]
    [TestCase("SELECT * FROM orders", "email_address")]
    public async Task PiiDetectionOn_MasksPiiResultColumns_InTheJoinedOutput(string sql, string resultColumn)
    {
        Generates(sql);
        ReturnsRows(new Dictionary<string, object?> { [resultColumn] = "alice@example.com", ["total"] = 5 });

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", PiiSettings(detect: true), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        text.Should().NotContain("alice@example.com");
        text.Should().Contain("a***m");
    }

    // SC7: the result key "contact" is not PII by name and the SQL-text match is "email" — only the source SQL's alias
    // resolution, carried into the join query by name, masks it.
    [TestCase("SELECT email AS contact, total FROM orders")]
    [TestCase("SELECT lower(o.email) AS contact, o.total FROM orders o")]
    public async Task PiiDetectionOn_MasksAnAliasedPiiColumn_InTheJoinedOutput(string sql)
    {
        Generates(sql);
        ReturnsRows(new Dictionary<string, object?> { ["contact"] = "alice@example.com", ["total"] = 5 });

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", PiiSettings(detect: true), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        text.Should().NotContain("alice@example.com");
        text.Should().Contain("a***m");
    }

    // F1: address_id matches the unanchored PII pattern. Masked before the join, 12345 and 10005 both become "1***5",
    // collide, and the join cross-matches into 4 rows; the keys must reach the join raw and only the output be masked.
    [Test]
    public async Task TwoSourceJoin_OnAKeyMatchingThePiiPattern_JoinsTheRawKeys_AndMasksThePiiOutput()
    {
        GeneratesFor(WarehouseContext, "SELECT * FROM orders");
        GeneratesFor(CrmContext, "SELECT address_id, city FROM addresses");
        ReturnsRowsFor(
            DataSourceId,
            Row(("address_id", 12345L), ("email", "alice@example.com")),
            Row(("address_id", 10005L), ("email", "bob@example.com")));
        ReturnsRowsFor(
            CrmDataSourceId,
            Row(("address_id", 12345L), ("city", "Zagreb")),
            Row(("address_id", 10005L), ("city", "Split")));
        JoinsWith("SELECT r1.email, r1.email AS contact, r2.city FROM result1 r1 JOIN result2 r2 ON r1.address_id = r2.address_id ORDER BY r2.city");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, TwoSources(), ProjectId, "q", PiiSettings(detect: true), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        text.Should().Contain("### Final Results (2 rows", "distinct join keys must not collide");
        text.Should().NotContain("alice@example.com").And.NotContain("bob@example.com");
        ResultLine(text, "Zagreb").Should().Be("| a***m | a***m | Zagreb |");
        ResultLine(text, "Split").Should().Be("| b***m | b***m | Split |");
    }

    [Test]
    public async Task TwoSourceJoin_ASourcesAliasedPiiColumn_StaysMaskedThroughTheJoin()
    {
        GeneratesFor(WarehouseContext, "SELECT address_id, email AS contact FROM orders");
        GeneratesFor(CrmContext, "SELECT address_id, city FROM addresses");
        ReturnsRowsFor(
            DataSourceId,
            Row(("address_id", 12345L), ("contact", "alice@example.com")),
            Row(("address_id", 10005L), ("contact", "bob@example.com")));
        ReturnsRowsFor(
            CrmDataSourceId,
            Row(("address_id", 12345L), ("city", "Zagreb")),
            Row(("address_id", 10005L), ("city", "Split")));
        JoinsWith("SELECT r1.contact, r1.contact AS who, r2.city FROM result1 r1 JOIN result2 r2 ON r1.address_id = r2.address_id ORDER BY r2.city");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, TwoSources(), ProjectId, "q", PiiSettings(detect: true), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        text.Should().Contain("### Final Results (2 rows", "distinct join keys must not collide");
        text.Should().NotContain("alice@example.com").And.NotContain("bob@example.com");
        ResultLine(text, "Zagreb").Should().Be("| a***m | a***m | Zagreb |");
        ResultLine(text, "Split").Should().Be("| b***m | b***m | Split |");
    }

    // F-3: a host-masked column is masked BEFORE its rows enter the in-memory join store. With PII detection off the
    // joined output gets no masking of its own, so a join query reading ref_code back under another name (x) would
    // otherwise return the raw value.
    [Test]
    public async Task TwoSourceJoin_AHostMaskedColumn_IsMaskedBeforeTheJoin_EvenUnderAnAlias()
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check(HostKey, It.IsAny<string>()))
            .Returns(new HostPolicyResult(true, null, ["ref_code"]));
        GeneratesFor(WarehouseContext, "SELECT * FROM orders");
        GeneratesFor(CrmContext, "SELECT address_id, city FROM addresses");
        ReturnsRowsFor(DataSourceId, Row(("address_id", 12345L), ("ref_code", "123456789")));
        ReturnsRowsFor(CrmDataSourceId, Row(("address_id", 12345L), ("city", "Zagreb")));
        JoinsWith("SELECT r1.ref_code, r1.ref_code AS x, r2.city FROM result1 r1 JOIN result2 r2 ON r1.address_id = r2.address_id");

        var (text, succeeded) = await CreateService(hostGuard.Object).ExecuteAsync(
            _llm.Object, TwoSources(), ProjectId, "q", PiiSettings(detect: false), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        hostGuard.Verify(x => x.Check(HostKey, It.IsAny<string>()), Times.AtLeastOnce(), "the warehouse source is host-managed");
        text.Should().NotContain("123456789");
        ResultLine(text, "Zagreb").Should().Be("| 1***9 | 1***9 | Zagreb |");
    }

    [Test]
    public async Task PiiDetectionOff_DoesNotMaskResultColumns()
    {
        Generates("SELECT * FROM orders");
        ReturnsRows(new Dictionary<string, object?> { ["customer_email"] = "alice@example.com", ["total"] = 5 });

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", PiiSettings(detect: false), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        text.Should().Contain("alice@example.com");
    }

    private void ReturnsRows(Dictionary<string, object?> row)
    {
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [row] });
    }

    private void ReturnsRowsFor(int dataSourceId, params Dictionary<string, object?>[] rows)
    {
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.Is<DataSource>(y => y.Id == dataSourceId), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = rows.ToList() });
    }

    private void GeneratesFor(string schemaContext, string sql)
    {
        _sqlGen
            .Setup(x => x.GenerateAsync(
                It.IsAny<ILlmProvider>(), schemaContext, It.IsAny<string>(),
                It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(sql, []));
    }

    private void JoinsWith(string joinSql)
    {
        _llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = joinSql });
    }

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value);

    private static string ResultLine(string text, string marker) =>
        text
            .Split('\n')
            .Single(x => x.Contains(marker));

    private static McpSettingsData PiiSettings(bool detect) =>
        new() { MaxRowLimit = MaxRowLimit, EnableSemanticLint = false, EnablePiiDetection = detect };

    private void Generates(string sql)
    {
        _sqlGen
            .Setup(x => x.GenerateAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(sql, ["orders"]));
    }

    private static List<RoutedSource> Sources() =>
        [new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "warehouse", Reason = "test" }];

    private static List<RoutedSource> TwoSources() =>
    [
        new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "warehouse", Reason = "test" },
        new RoutedSource { DataSourceId = CrmDataSourceId, DataSourceName = "crm", Reason = "test" }
    ];

    private static McpSettingsData Settings() => new() { MaxRowLimit = MaxRowLimit, EnableSemanticLint = false };

    private static Dictionary<string, HashSet<string>> Catalog() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["orders"] = new(StringComparer.OrdinalIgnoreCase) { "id", "customer_id", "total", "email", "address_id" }
    };

    private static Dictionary<string, HashSet<string>> CrmCatalog() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["addresses"] = new(StringComparer.OrdinalIgnoreCase) { "address_id", "city" }
    };

    // A host guard makes the warehouse source host-managed under HostKey.
    private CrossSourceQueryService CreateService(IHostDataSourceGuard? hostGuard = null)
    {
        var warehouseHostKey = hostGuard == null ? null : HostKey;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CrossSourceTestContext(warehouseHostKey));

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(_provider.Object);

        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = WarehouseContext,
                DatabaseDialect = "PostgreSQL",
                SchemaCatalog = Catalog()
            });
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(CrmDataSourceId, ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = CrmContext,
                DatabaseDialect = "PostgreSQL",
                SchemaCatalog = CrmCatalog()
            });

        var guardrail = new QueryGuardrailService();

        return new CrossSourceQueryService(
            factory.Object,
            providerFactory.Object,
            guardrail,
            TestSqlGate.Create(guardrail, hostGuard),
            knowledgeGraph.Object,
            _sqlGen.Object,
            NullLoggerFactory.Instance,
            NullLogger<CrossSourceQueryService>.Instance);
    }

    /// <summary>Serves the data-source lookup over an async-queryable double — no DB (§4.7).</summary>
    private sealed class CrossSourceTestContext : BeaconContext
    {
        private static readonly DbContextOptions<CrossSourceTestContext> Options =
            new DbContextOptionsBuilder<CrossSourceTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly string? _warehouseHostKey;

        public CrossSourceTestContext(string? warehouseHostKey) : base(Options, "beacon")
        {
            _warehouseHostKey = warehouseHostKey;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>
                {
                    new()
                    {
                        Id = DataSourceId,
                        Name = "warehouse",
                        DataSourceType = DataSourceType.Database,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                        HostManagedKey = _warehouseHostKey
                    },
                    new()
                    {
                        Id = CrmDataSourceId,
                        Name = "crm",
                        DataSourceType = DataSourceType.Database,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = DatabaseEngineType.PostgreSQL
                    }
                });
            }

            return base.Set<TEntity>();
        }

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
