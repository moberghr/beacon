using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Under its size budget the schema context an <c>ask</c> sends is byte-identical to the context rendered before the budget
/// existed, with every grounding section present: join paths, value matches, verified examples, learned patterns and the
/// glossary. The goldens in <c>AskSchemaContextGolden/</c> were recorded from the rendering without a budget; re-record
/// only on purpose with <c>BEACON_RECORD_ASK_CONTEXT_GOLDEN=&lt;golden dir&gt;</c> and check the diff.
/// </summary>
[TestFixture]
public class AskSchemaContextGoldenTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;
    private const string Question = "how many orders per customer";
    private const string ValueMatches = "\n## Value matches\n- sales.orders.status: 'shipped'\n";

    [Test]
    public async Task SmallSchemaContext_WithEveryGroundingSection_MatchesTheGolden()
    {
        var result = await (await ServiceAsync(SalesTables())).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.UsedSmartRetrieval.Should().BeFalse();
        AssertEverySection(result.FullContext);
        MatchesGolden("small-schema.txt", result.FullContext);
    }

    [Test]
    public async Task LargeSchemaContext_WithEveryGroundingSection_MatchesTheGolden()
    {
        var tables = SalesTables();
        tables.AddRange(Enumerable.Range(1, 45).Select(x => Table(100 + x, "public", $"t{x:D4}", ("id", true), ("note", false))));

        var result = await (await ServiceAsync(tables)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.UsedSmartRetrieval.Should().BeTrue();
        AssertEverySection(result.FullContext);
        result.FullContext.Should().Contain("## Other Tables (name and primary keys only)");
        MatchesGolden("large-schema.txt", result.FullContext);
    }

    [Test]
    public async Task SmallSchemaContext_OverTheBudget_RanksTheTableTheQuestionNames_NotATableAnExampleKeywordNames()
    {
        // The verified example's SQL says ORDER BY and names sales.customers: neither ranks a table any more; the question
        // names "orders", which ranks first, then the catalogue order.
        var tables = SalesTables();
        tables.Add(Table(4, "sales", "order", new string('o', 2_500), ("id", true)));
        tables[2] = Table(3, "sales", "order_items", new string('i', 2_500), ("order_id", false));
        var unbounded = await (await ServiceAsync(tables)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var leftOutChars = Rendered(tables[2]).Length + Rendered(tables[3]).Length;
        var budget = unbounded.FullContext.Length - leftOutChars + AskSchemaContextBudget.OmittedTablesLineMaxChars(tables.Count);

        var result = await (await ServiceAsync(tables, budget)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.TablesNotShown.Should().Equal("sales.order", "sales.order_items");
        result.FullContext.Should().Contain(Rendered(tables[1])).And.Contain(Rendered(tables[0]));
        result.FullContext.Should().Contain("## Verified query examples (authoritative)", "every grounding section still fits");
        result.FullContext.Length.Should().BeLessThanOrEqualTo(budget);
    }

    [Test]
    public async Task SmallSchemaContext_GroundingLeavesNoRoomForTheTopTable_LeavesSectionsOut_AndWarnsWithCountsOnly()
    {
        var tables = SalesTables();
        tables.Add(Table(4, "sales", "zz_archive", new string('a', 4_000), ("id", true)));
        var head = new System.Text.StringBuilder();
        head.AppendLine("# Data Source: Sales DB (PostgreSQL)");
        head.AppendLine($"Tables: {tables.Count}\n");
        var orders = Rendered(tables[1]);
        var budget = head.Length + AskSchemaContextBudget.OmittedTablesLineMaxChars(tables.Count) + orders.Length + 10;
        using var logs = new LogRecorder();

        var result = await (await ServiceAsync(tables, budget, logs)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Be(head + orders + AskSchemaContextBudget.OmittedTablesLine(["sales.customers", "sales.order_items", "sales.zz_archive"]) + Environment.NewLine);
        result.TablesShown.Should().Be(1);
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("5 grounding section(s) left out").And.Contain("1 table(s) shown").And.Contain("3 left out");
        warning.Message.Should().NotContain("sales.").And.NotContain("orders");
    }

    private static void AssertEverySection(string context)
    {
        context.Should().Contain("## Join Paths (verified)");
        context.Should().Contain("## Value matches");
        context.Should().Contain("## Verified query examples (authoritative)");
        context.Should().Contain("## Learned Patterns (from usage)");
        context.Should().Contain("## Business Glossary");
    }

    private static void MatchesGolden(string fileName, string actual)
    {
        var recordDir = Environment.GetEnvironmentVariable("BEACON_RECORD_ASK_CONTEXT_GOLDEN");
        if (!string.IsNullOrEmpty(recordDir))
        {
            File.WriteAllText(Path.Combine(recordDir, fileName), actual);
        }

        var expected = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Unit", "AskSchemaContextGolden", fileName));
        actual.Should().Be(expected);
    }

    private static string Rendered(DatabaseMetadata table)
    {
        var sb = new System.Text.StringBuilder();
        var columns = table.Columns
            .Select(x => new SchemaColumn(x.ColumnName, x.DataType, x.IsPrimaryKey, x.IsNullable, x.ForeignKeyTable, x.ForeignKeyColumn, x.Description))
            .ToList();
        SchemaContextFormatter.AppendTableWithFullColumns(sb, table.SchemaName, table.TableName, table.TableDescription, columns, isApi: false);
        return sb.ToString();
    }

    private static List<DatabaseMetadata> SalesTables()
    {
        return
        [
            Table(1, "sales", "customers", ("id", true), ("name", false)),
            Table(2, "sales", "orders", ("id", true), ("customer_id", false), ("status", false)),
            Table(3, "sales", "order_items", ("order_id", false), ("product", false), ("quantity", false))
        ];
    }

    private static DatabaseMetadata Table(int id, string schema, string name, params (string Name, bool IsPrimaryKey)[] columns)
    {
        return Table(id, schema, name, $"The {name} table", columns);
    }

    private static DatabaseMetadata Table(int id, string schema, string name, string description, params (string Name, bool IsPrimaryKey)[] columns)
    {
        var table = new DatabaseMetadata
        {
            Id = id,
            DataSourceId = DataSourceId,
            DataSource = SalesDb,
            SchemaName = schema,
            TableName = name,
            TableDescription = description,
            Columns = columns
                .Select((x, i) => new ColumnMetadata
                {
                    Id = id * 100 + i,
                    ColumnName = x.Name,
                    DataType = x.IsPrimaryKey ? "integer" : "text",
                    IsPrimaryKey = x.IsPrimaryKey,
                    IsNullable = !x.IsPrimaryKey
                })
                .ToList()
        };

        // The column search reads a column's table through its navigation.
        foreach (var column in table.Columns)
        {
            column.DatabaseMetadataId = id;
            column.DatabaseMetadata = table;
        }

        return table;
    }

    private static readonly DataSource SalesDb = new()
    {
        Id = DataSourceId,
        Name = "Sales DB",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "unused-in-test"
    };

    private static async Task<KnowledgeGraphService> ServiceAsync(List<DatabaseMetadata> tables, int? budgetChars = null, LogRecorder? logs = null)
    {
        var embedder = new FakeEmbeddingService();
        var questionVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);
        var patterns = new List<McpLearnedPattern>
        {
            new()
            {
                Id = 800,
                DataSourceId = DataSourceId,
                ProjectId = ProjectId,
                SchemaName = "sales",
                TableName = "orders",
                PatternType = McpPatternType.CommonQuery,
                PatternContent = "orders are counted per customer_id",
                Status = McpPatternStatus.Approved,
                ExampleQuestion = "orders per customer",
                ExampleSql = "SELECT customer_id, count(*) FROM sales.orders GROUP BY customer_id",
                Confidence = 0.9
            }
        };
        var cases = new List<McpEvalCase>
        {
            new()
            {
                Id = 700,
                ProjectId = ProjectId,
                DataSourceId = DataSourceId,
                Question = Question,
                GoldSql = "SELECT c.name, count(*) FROM sales.orders o JOIN sales.customers c ON c.id = o.customer_id GROUP BY c.name ORDER BY 2 DESC",
                IsActive = true
            }
        };
        var terms = new List<McpGlossaryTerm>
        {
            new()
            {
                Id = 600,
                ProjectId = ProjectId,
                Term = "active customer",
                Definition = "A customer with an order in the last 90 days",
                TargetSchema = "sales",
                TargetTable = "customers",
                IsActive = true
            }
        };
        var embeddings = new List<McpEmbedding>
        {
            Embedding(900, McpEmbeddingOwnerType.Exemplar, 800, await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask("orders per customer"), CancellationToken.None)),
            Embedding(901, McpEmbeddingOwnerType.GoldenCase, 700, questionVector),
            Embedding(902, McpEmbeddingOwnerType.GlossaryTerm, 600, questionVector)
        };
        var settings = new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableLearning = true,
            EnableSemanticRetrieval = true,
            EnableValueGrounding = true,
            GoldenExemplarTopK = 5,
            ExemplarTopK = 5,
            GlossaryTopK = 5
        };

        var context = new GoldenTestContext(tables, patterns, cases, embeddings, terms);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var settingsProvider = SettingsProviderMock.Create(settings);
        var graph = SchemaGraph.Build(
            SalesTables()
                .Select(x => new TableMetadataDto(x.SchemaName, x.TableName, [], [], null))
                .ToList(),
            [
                new SchemaRelationshipEdge("sales", "orders", "customer_id", "sales", "customers", "id",
                    "customer", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0),
                new SchemaRelationshipEdge("sales", "order_items", "order_id", "sales", "orders", "id",
                    "order", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0)
            ]);

        return new KnowledgeGraphService(
            factory.Object,
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            embedder,
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()) == Task.FromResult(graph)),
            Mock.Of<IValueGroundingService>(x => x.BuildValueMatchesBlockAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ValueGroundingTable>>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>())
                == Task.FromResult(ValueMatches)),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            logs?.For<KnowledgeGraphService>() ?? NullLogger<KnowledgeGraphService>.Instance,
            deploymentOptions: budgetChars == null
                ? null
                : Options.Create(new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskSchemaContextChars = budgetChars } }));
    }

    private static McpEmbedding Embedding(int id, McpEmbeddingOwnerType ownerType, int ownerId, float[] vector)
    {
        return new McpEmbedding
        {
            Id = id,
            DataSourceId = ownerType == McpEmbeddingOwnerType.GlossaryTerm ? null : DataSourceId,
            ProjectId = ProjectId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set.Setup(x => x.AsQueryable()).Returns(() => set.Object);
        return set;
    }

    /// <summary>The data source linked to the caller's project, its metadata and every grounding set.</summary>
    private sealed class GoldenTestContext : BeaconContext
    {
        private static readonly DbContextOptions<GoldenTestContext> ContextOptions =
            new DbContextOptionsBuilder<GoldenTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Dictionary<Type, object> _sets;
        private DatabaseFacade? _database;

        public GoldenTestContext(
            List<DatabaseMetadata> metadata,
            List<McpLearnedPattern> patterns,
            List<McpEvalCase> cases,
            List<McpEmbedding> embeddings,
            List<McpGlossaryTerm> terms) : base(ContextOptions, "beacon")
        {
            _sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = BuildDbSet(new List<DataSource> { SalesDb }).Object,
                [typeof(DatabaseMetadata)] = BuildDbSet(metadata).Object,
                [typeof(ProjectDataSource)] = BuildDbSet(new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = DataSourceId } }).Object,
                [typeof(McpLearnedPattern)] = BuildDbSet(patterns).Object,
                [typeof(McpEvalCase)] = BuildDbSet(cases).Object,
                [typeof(McpEmbedding)] = BuildDbSet(embeddings).Object,
                [typeof(McpGlossaryTerm)] = BuildDbSet(terms).Object,
                [typeof(ColumnMetadata)] = BuildDbSet(metadata.SelectMany(x => x.Columns).ToList()).Object,
                [typeof(ProjectDocumentationSection)] = BuildDbSet(new List<ProjectDocumentationSection>()).Object
            };
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            return _sets.TryGetValue(typeof(TEntity), out var set)
                ? (DbSet<TEntity>)set
                : base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
