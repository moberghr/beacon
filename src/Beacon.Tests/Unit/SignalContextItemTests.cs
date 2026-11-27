using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Provenance of an <c>ask</c>: the golden cases, learned patterns and glossary terms its prompt carried are reported by id,
/// in rendered order, and saved as the signal's <see cref="McpSignalContextItem"/> rows. Runs the real
/// <see cref="KnowledgeGraphService"/> over a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles
/// (§4.7) with the in-memory cosine search and <see cref="FakeEmbeddingService"/>.
/// </summary>
[TestFixture]
public class SignalContextItemTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;
    private const int OtherProjectId = 99;
    private const string Question = "How many orders were refunded last month?";
    private const string Dialect = "PostgreSQL";

    // ---------- the block builders ----------

    [Test]
    public async Task GoldenExemplarBlockWithItems_ReportsTheRenderedActiveCasesOfTheProject_InSimilarityOrder()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await QueryVectorAsync(embedder);
        var vectors = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("refunded orders count", CancellationToken.None),
            [2] = await embedder.EmbedAsync("revenue by region", CancellationToken.None),
            [3] = await embedder.EmbedAsync("active customers list", CancellationToken.None)
        };
        var expected = vectors
            .OrderByDescending(x => EmbeddingCodec.Cosine(queryVector, x.Value))
            .Select(x => x.Key)
            .ToList();
        var cases = new List<McpEvalCase>
        {
            NewCase(1, ProjectId, "SELECT count(*) FROM t_case_1"),
            NewCase(2, ProjectId, "SELECT count(*) FROM t_case_2"),
            NewCase(3, ProjectId, "SELECT count(*) FROM t_case_3"),
            NewCase(4, ProjectId, "SELECT count(*) FROM t_case_4", isActive: false),
            NewCase(5, OtherProjectId, "SELECT count(*) FROM t_case_5")
        };
        var embeddings = vectors
            .Select(x => NewEmbedding(500 + x.Key, McpEmbeddingOwnerType.GoldenCase, x.Key, x.Value, ProjectId))
            .Append(NewEmbedding(504, McpEmbeddingOwnerType.GoldenCase, 4, queryVector, ProjectId))
            .Append(NewEmbedding(505, McpEmbeddingOwnerType.GoldenCase, 5, queryVector, OtherProjectId))
            .ToList();
        var settings = GoldenSettings(budgetChars: 4_000);
        var service = Service(embedder, new KnowledgeTestContext(embeddings, cases: cases), settings);

        var block = await service.BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        block.Items.Select(x => x.ItemId).Should().Equal(expected, "the nearest active cases of the caller's project, nearest first");
        block.Items.Should().OnlyContain(x => x.Kind == McpKnowledgeItemKind.GoldenCase);
        block.Items.Select(x => x.Rank).Should().Equal(1, 2, 3);
        CaseIdsIn(block.Text, 5).Should().Equal(expected, "the items are the cases the text renders, in its order");
        block.Text.Should().Be(await service.BuildGoldenExemplarBlockAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None));
    }

    [Test]
    public async Task GoldenExemplarBlockWithItems_CasesTheBudgetLeavesOut_AreNotReported()
    {
        var embedder = new FakeEmbeddingService();
        var cases = Enumerable.Range(1, 3)
            .Select(x => NewCase(x, ProjectId, BigSql(x)))
            .ToList();
        var embeddings = new List<McpEmbedding>();
        foreach (var goldenCase in cases)
        {
            embeddings.Add(NewEmbedding(500 + goldenCase.Id, McpEmbeddingOwnerType.GoldenCase, goldenCase.Id, await embedder.EmbedAsync($"q{goldenCase.Id}", CancellationToken.None), ProjectId));
        }

        var settings = GoldenSettings(budgetChars: BigSql(1).Length + 100);
        var service = Service(embedder, new KnowledgeTestContext(embeddings, cases: cases), settings);

        var block = await service.BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        var rendered = CaseIdsIn(block.Text, 3);
        rendered.Should().NotBeEmpty().And.HaveCountLessThan(3, "the budget trims the later cases");
        block.Items.Select(x => x.ItemId).Should().Equal(rendered);
        block.Items.Select(x => x.Rank).Should().Equal(Enumerable.Range(1, rendered.Count));
    }

    [Test]
    public async Task GlossaryBlockWithItems_ReportsTheRenderedActiveTermsOfTheProject_InSimilarityOrder()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await QueryVectorAsync(embedder);
        var vectors = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("refund count", CancellationToken.None),
            [2] = await embedder.EmbedAsync("revenue by region", CancellationToken.None),
            [3] = await embedder.EmbedAsync("active customers", CancellationToken.None)
        };
        var expected = vectors
            .OrderByDescending(x => EmbeddingCodec.Cosine(queryVector, x.Value))
            .Select(x => x.Key)
            .ToList();
        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, ProjectId),
            NewTerm(2, ProjectId),
            NewTerm(3, ProjectId),
            NewTerm(4, ProjectId, isActive: false),
            NewTerm(5, OtherProjectId)
        };
        var embeddings = vectors
            .Select(x => NewEmbedding(600 + x.Key, McpEmbeddingOwnerType.GlossaryTerm, x.Key, x.Value, ProjectId, dataSourceId: 0))
            .Append(NewEmbedding(604, McpEmbeddingOwnerType.GlossaryTerm, 4, queryVector, ProjectId, dataSourceId: 0))
            .Append(NewEmbedding(605, McpEmbeddingOwnerType.GlossaryTerm, 5, queryVector, OtherProjectId, dataSourceId: 0))
            .ToList();
        var settings = new McpSettingsData
        {
            EnableSemanticRetrieval = true,
            GlossaryTopK = 10
        };
        var service = Service(embedder, new KnowledgeTestContext(embeddings, terms: terms), settings);

        var block = await service.BuildGlossaryBlockWithItemsAsync(ProjectId, Question, settings, Dialect, CancellationToken.None);

        block.Items.Select(x => x.ItemId).Should().Equal(expected, "the nearest active terms of the caller's project, nearest first");
        block.Items.Should().OnlyContain(x => x.Kind == McpKnowledgeItemKind.GlossaryTerm);
        block.Items.Select(x => x.Rank).Should().Equal(1, 2, 3);
        TermIdsIn(block.Text, 5).Should().Equal(expected, "the items are the terms the text renders, in its order");
        block.Text.Should().Be(await service.BuildGlossaryBlockAsync(ProjectId, Question, settings, Dialect, CancellationToken.None));
    }

    [Test]
    public void LearnedPatternItems_FollowTheFormattersSectionOrder_AndLeaveOutAPatternItDoesNotRender()
    {
        var patterns = new List<LearnedPatternInfo>
        {
            Pattern(11, nameof(McpPatternType.CommonQuery)),
            Pattern(12, nameof(McpPatternType.SchemaCorrection)),
            Pattern(13, nameof(McpPatternType.JoinPattern)),
            Pattern(14, nameof(McpPatternType.CommonQuery)),
            Pattern(15, nameof(McpPatternType.ColumnClarification)),
            Pattern(16, "NotAType")
        };

        var items = KnowledgeGraphService.LearnedPatternItems(patterns);

        var text = KnowledgeGraphService.FormatLearnedPatternsForLlm(patterns);
        var renderedOrder = patterns
            .Where(x => text.Contains($"content-{x.Id}", StringComparison.Ordinal))
            .OrderBy(x => text.IndexOf($"content-{x.Id}", StringComparison.Ordinal))
            .Select(x => x.Id)
            .ToList();
        renderedOrder.Should().Equal(12, 15, 13, 11, 14);
        items.Select(x => x.ItemId).Should().Equal(renderedOrder);
        items.Should().OnlyContain(x => x.Kind == McpKnowledgeItemKind.LearnedPattern);
        items.Select(x => x.Rank).Should().Equal(1, 2, 3, 4, 5);
    }

    // ---------- the ask context ----------

    [Test]
    public async Task SmartContext_EveryBlockShown_ReportsTheGoldenCasePatternAndTerm()
    {
        var (service, _) = await AskContextScenarioAsync(budgetChars: null);

        var result = await service.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Contain("## Verified query examples").And.Contain("## Learned Patterns").And.Contain("## Business Glossary");
        result.KnowledgeItems.Should().Equal(
            new KnowledgeItemRef(McpKnowledgeItemKind.GoldenCase, 700, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.LearnedPattern, 800, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 900, 1));
    }

    [Test]
    public async Task SmartContext_TheBudgetLeavesOutTheLearnedPatterns_ReportsNoPattern()
    {
        var (ample, tables) = await AskContextScenarioAsync(budgetChars: null);
        var full = (await ample.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None)).FullContext;
        var patternsChars = full.IndexOf("\n## Business Glossary", StringComparison.Ordinal) - full.IndexOf("\n## Learned Patterns", StringComparison.Ordinal);
        var otherTableChars = Rendered(tables[1]).Length;
        var omittedLine = AskSchemaContextBudget.OmittedTablesLineMaxChars(tables.Count);
        full.Should().Contain(Rendered(tables[1]));
        (otherTableChars + patternsChars).Should().BeGreaterThan(omittedLine, "the scenario must be over the budget");

        // Room for the head, the question's table and every part but the learned patterns: only they are left out.
        var (tight, _) = await AskContextScenarioAsync(budgetChars: full.Length - otherTableChars + omittedLine - patternsChars);

        var result = await tight.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().NotContain("## Learned Patterns");
        result.FullContext.Should().Contain("## Verified query examples").And.Contain("## Business Glossary");
        result.KnowledgeItems.Should().Equal(
            new KnowledgeItemRef(McpKnowledgeItemKind.GoldenCase, 700, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 900, 1));
    }

    // ---------- the signal ----------

    [Test]
    public void Build_KnowledgeItems_BecomeContextItems_OnePerKindAndId_FirstRankKept()
    {
        var builder = new McpSignalBuilder()
            .SetTool("ask")
            .SetQuestion(Question)
            .SetKnowledgeItems(
            [
                new KnowledgeItemRef(McpKnowledgeItemKind.GoldenCase, 5, 1),
                new KnowledgeItemRef(McpKnowledgeItemKind.LearnedPattern, 8, 1),
                new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 2, 1),
                new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 2, 3),
                new KnowledgeItemRef(McpKnowledgeItemKind.GoldenCase, 5, 2),
                new KnowledgeItemRef(McpKnowledgeItemKind.LearnedPattern, 5, 2)
            ]);

        var signal = builder.Build();

        signal.ContextItems
            .Select(x => (x.Kind, x.ItemId, x.Rank))
            .Should()
            .Equal(
                (McpKnowledgeItemKind.GoldenCase, 5, 1),
                (McpKnowledgeItemKind.LearnedPattern, 8, 1),
                (McpKnowledgeItemKind.GlossaryTerm, 2, 1),
                (McpKnowledgeItemKind.LearnedPattern, 5, 2));
    }

    [Test]
    public void Build_WithoutKnowledgeItems_HasNoContextItems()
    {
        var signal = new McpSignalBuilder()
            .SetTool("ask")
            .SetQuestion(Question)
            .SetGeneratedSql("SELECT 1")
            .Build();

        signal.ContextItems.Should().BeEmpty();
        signal.GeneratedSql.Should().Be("SELECT 1");
    }

    [Test]
    public async Task AskTool_SavesTheOutcomesKnowledgeItems_AsTheSignalsContextItems()
    {
        IReadOnlyList<KnowledgeItemRef> items =
        [
            new KnowledgeItemRef(McpKnowledgeItemKind.GoldenCase, 700, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 900, 1)
        ];
        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        await AskTool(Outcome() with { KnowledgeItems = items })
            .GenerateAndExecuteSqlAsync(Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData(), true, signal, null, CancellationToken.None);

        signal.Build().ContextItems
            .Select(x => (x.Kind, x.ItemId, x.Rank))
            .Should()
            .Equal((McpKnowledgeItemKind.GoldenCase, 700, 1), (McpKnowledgeItemKind.GlossaryTerm, 900, 1));
    }

    [Test]
    public async Task AskTool_AnOutcomeWithoutKnowledgeItems_SavesASignalWithoutContextItems()
    {
        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        await AskTool(Outcome())
            .GenerateAndExecuteSqlAsync(Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData(), true, signal, null, CancellationToken.None);

        var built = signal.Build();
        built.GeneratedSql.Should().Be("SELECT count(*) FROM orders", "the outcome reached the signal");
        built.ContextItems.Should().BeEmpty();
    }

    [Test]
    public async Task AskPipeline_CarriesTheSchemaContextsKnowledgeItems_OntoTheOutcome()
    {
        IReadOnlyList<KnowledgeItemRef> items =
        [
            new KnowledgeItemRef(McpKnowledgeItemKind.GoldenCase, 700, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.LearnedPattern, 800, 1)
        ];
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "# Data Source: Sales\n### public.orders\n",
                DatabaseDialect = Dialect,
                TotalTableCount = 1,
                TablesShown = 1,
                KnowledgeItems = items
            });
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult("SELECT count(*) FROM public.orders", ["public.orders"]));
        var executor = new Mock<IAskSqlExecutor>();
        executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));
        var pipeline = new AskSqlPipeline(
            knowledgeGraph.Object,
            sqlGeneration.Object,
            TestSqlGate.Create(guardrail.Object),
            TestRowSecurity.Resolver,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
            NullLogger<AskSqlPipeline>.Instance);

        var outcome = await pipeline.RunAsync(
            Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData { EnableSemanticLint = false }, executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.KnowledgeItems.Should().Equal(items);
    }

    private static async Task<(KnowledgeGraphService Service, List<DatabaseMetadata> Tables)> AskContextScenarioAsync(int? budgetChars)
    {
        var embedder = new FakeEmbeddingService();
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "Sales DB",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "unused-in-test"
        };
        var tables = new List<DatabaseMetadata>
        {
            Table(dataSource, 1, "orders", ["id", "status"]),
            Table(dataSource, 2, "audit_log", Enumerable.Range(1, 120).Select(x => $"audit_column_{x:D3}").ToList())
        };
        var patterns = new List<McpLearnedPattern>
        {
            new()
            {
                Id = 800,
                DataSourceId = DataSourceId,
                ProjectId = ProjectId,
                SchemaName = "public",
                TableName = "orders",
                PatternType = McpPatternType.CommonQuery,
                PatternContent = "orders refund pattern",
                Status = McpPatternStatus.Approved,
                ExampleQuestion = "orders refunded",
                ExampleSql = "SELECT count(*) FROM orders WHERE status = 4",
                Confidence = 0.9
            }
        };
        var embeddings = new List<McpEmbedding>
        {
            NewEmbedding(1, McpEmbeddingOwnerType.Exemplar, 800, await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask("orders refunded"), CancellationToken.None), ProjectId),
            NewEmbedding(2, McpEmbeddingOwnerType.GoldenCase, 700, await QueryVectorAsync(embedder), ProjectId),
            NewEmbedding(3, McpEmbeddingOwnerType.GlossaryTerm, 900, await QueryVectorAsync(embedder), ProjectId, dataSourceId: 0)
        };
        var context = new KnowledgeTestContext(
            embeddings,
            cases: [NewCase(700, ProjectId, "SELECT count(*) FROM orders WHERE status = 4")],
            terms: [NewTerm(900, ProjectId)],
            patterns: patterns,
            dataSource: dataSource,
            metadata: tables);
        var settings = new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableLearning = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5,
            ExemplarTopK = 5,
            GlossaryTopK = 5
        };

        return (Service(embedder, context, settings, budgetChars), tables);
    }

    private static KnowledgeGraphService Service(IBeaconEmbeddingService embedder, BeaconContext context, McpSettingsData settings, int? budgetChars = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var settingsProvider = SettingsProviderMock.Create(settings);
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);
        var deployment = budgetChars == null
            ? null
            : Options.Create(new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskSchemaContextChars = budgetChars } });

        return new KnowledgeGraphService(
            factory.Object,
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            embedder,
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
            Mock.Of<IValueGroundingService>(x => x.BuildValueMatchesBlockAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ValueGroundingTable>>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>())
                == Task.FromResult("")),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance,
            deploymentOptions: deployment);
    }

    private static ProjectAskTool AskTool(AskSqlOutcome outcome)
    {
        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(
                It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(),
                It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        return TestToolCalls.Track(recorder => new ProjectAskTool(
            Mock.Of<IKnowledgeGraphService>(),
            SettingsProviderMock.Create().Object,
            SettingsProviderMock.Unshared(SettingsProviderMock.Create().Object),
            new Mock<IServiceProvider>().WithoutAskLimit().Object,
            Mock.Of<IProjectContext>(),
            recorder,
            pipeline.Object,
            Mock.Of<IAskSqlExecutor>(),
            Mock.Of<IIntentClassifier>(),
            Mock.Of<IDataSourceRouter>(),
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            new McpQueryConcurrencyLimiter(),
            TimeProvider.System,
            NullLogger<ProjectAskTool>.Instance), () => TestToolCalls.Recorder(null!, null!));
    }

    private static AskSqlOutcome Outcome()
    {
        const string sql = "SELECT count(*) FROM orders";

        return new AskSqlOutcome(sql, sql, ["orders"], true, "### Generated SQL\n", new AskExecutionResult("### Results (1 rows)\n", null, 1, true), null, null, null, [], false, null, [], null, [], null, []);
    }

    private static async Task<float[]> QueryVectorAsync(FakeEmbeddingService embedder)
    {
        return await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);
    }

    private static McpSettingsData GoldenSettings(int budgetChars)
    {
        return new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5,
            GoldenExemplarBudgetChars = budgetChars
        };
    }

    private static string BigSql(int caseId)
    {
        return "SELECT " + string.Join(", ", Enumerable.Range(0, 40).Select(x => $"col_{x}")) + $" FROM t_case_{caseId}";
    }

    // The case ids whose gold SQL the block renders (each reads its own t_case_{id} table), in rendered order.
    private static List<int> CaseIdsIn(string text, int maxId)
    {
        return Enumerable.Range(1, maxId)
            .Where(x => text.Contains($"FROM t_case_{x}", StringComparison.Ordinal))
            .OrderBy(x => text.IndexOf($"FROM t_case_{x}", StringComparison.Ordinal))
            .ToList();
    }

    private static List<int> TermIdsIn(string text, int maxId)
    {
        return Enumerable.Range(1, maxId)
            .Where(x => text.Contains($"**term-{x}**", StringComparison.Ordinal))
            .OrderBy(x => text.IndexOf($"**term-{x}**", StringComparison.Ordinal))
            .ToList();
    }

    private static LearnedPatternInfo Pattern(int id, string patternType)
    {
        return new LearnedPatternInfo(patternType, "orders", "status", $"content-{id}", null, null, 0.9, id);
    }

    private static McpEvalCase NewCase(int id, int projectId, string goldSql, bool isActive = true)
    {
        return new McpEvalCase
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            Question = $"question {id}",
            GoldSql = goldSql,
            IsActive = isActive
        };
    }

    private static McpGlossaryTerm NewTerm(int id, int projectId, bool isActive = true)
    {
        return new McpGlossaryTerm
        {
            Id = id,
            ProjectId = projectId,
            Term = $"term-{id}",
            Definition = $"definition {id}",
            IsActive = isActive
        };
    }

    private static McpEmbedding NewEmbedding(int id, McpEmbeddingOwnerType ownerType, int ownerId, float[] vector, int projectId, int dataSourceId = DataSourceId)
    {
        return new McpEmbedding
        {
            Id = id,
            DataSourceId = dataSourceId,
            ProjectId = projectId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };
    }

    private static DatabaseMetadata Table(DataSource dataSource, int id, string name, List<string> columns)
    {
        return new DatabaseMetadata
        {
            Id = id,
            DataSourceId = DataSourceId,
            DataSource = dataSource,
            SchemaName = "public",
            TableName = name,
            TableDescription = name,
            Columns = columns
                .Select((x, y) =>
                    new ColumnMetadata
                    {
                        Id = id * 1_000 + y,
                        ColumnName = x,
                        DataType = "integer",
                        IsPrimaryKey = y == 0,
                        IsNullable = y != 0
                    })
                .ToList()
        };
    }

    private static string Rendered(DatabaseMetadata table)
    {
        var columns = table.Columns
            .Select(x => new SchemaColumn(x.ColumnName, x.DataType, x.IsPrimaryKey, x.IsNullable, x.ForeignKeyTable, x.ForeignKeyColumn, x.Description))
            .ToList();
        var sb = new StringBuilder();
        SchemaContextFormatter.AppendTableWithFullColumns(sb, table.SchemaName, table.TableName, table.TableDescription, columns, isApi: false);

        return sb.ToString();
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(List<T> data) where T : class
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

    /// <summary>The data source (linked to the caller's project), its metadata and the grounding sets the ask context reads.</summary>
    private sealed class KnowledgeTestContext : BeaconContext
    {
        private static readonly DbContextOptions<KnowledgeTestContext> ContextOptions =
            new DbContextOptionsBuilder<KnowledgeTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Dictionary<Type, object> _sets = [];
        private DatabaseFacade? _database;

        public KnowledgeTestContext(
            List<McpEmbedding> embeddings,
            List<McpEvalCase>? cases = null,
            List<McpGlossaryTerm>? terms = null,
            List<McpLearnedPattern>? patterns = null,
            DataSource? dataSource = null,
            List<DatabaseMetadata>? metadata = null) : base(ContextOptions, "beacon")
        {
            AddSet(embeddings);
            AddSet(cases ?? []);
            AddSet(terms ?? []);
            AddSet(patterns ?? []);
            AddSet(dataSource == null ? [] : new List<DataSource> { dataSource });
            AddSet(metadata ?? []);
            AddSet(dataSource == null ? [] : new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = dataSource.Id } });
            AddSet(new List<ColumnMetadata>());
            AddSet(new List<ProjectDocumentationSection>());
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

        private void AddSet<T>(List<T> rows) where T : class
        {
            _sets[typeof(T)] = BuildDbSet(rows).Object;
        }
    }

    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
