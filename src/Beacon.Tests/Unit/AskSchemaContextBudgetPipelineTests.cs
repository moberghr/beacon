using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The ask pipeline sends the one size-bounded schema context it was given to every prompt: the first generation, the
/// self-consistency candidates and each repair. A repair over SQL that reads a table the budget left out of that context
/// gets the table's exact schema.
/// </summary>
[TestFixture]
public class AskSchemaContextBudgetPipelineTests
{
    private const int DataSourceId = 3;
    private const int ProjectId = 11;
    private const string Question = "revenue by region";
    private const string BoundedContext = "# Data Source: Sales\n### public.sales\n-- 1 more tables not shown: public.regions\n";
    private const string JoinSql = "SELECT r.name, SUM(s.amount) FROM public.sales s JOIN public.regions r ON r.id = s.region_id GROUP BY r.name";
    private const string SalesSql = "SELECT SUM(amount) FROM public.sales";
    private const string RegionsSchema = "# Table Schemas\n### public.regions\n";

    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private Mock<ISqlGenerationService> _sqlGeneration = null!;
    private Mock<IAskSqlExecutor> _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = BoundedContext,
                DatabaseDialect = "PostgreSQL",
                TotalTableCount = 2,
                TablesShown = 1,
                TablesNotShown = ["public.regions"]
            });
        _knowledgeGraph
            .Setup(x => x.GetTablesContextAsync(DataSourceId, ProjectId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RegionsSchema);

        _sqlGeneration = new Mock<ISqlGenerationService>();
        _executor = new Mock<IAskSqlExecutor>();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (3 rows)\n", null, 3, true));
    }

    [Test]
    public async Task RunAsync_SendsTheSameBoundedContext_ToGenerationCandidatesAndRepair()
    {
        var contexts = new List<(string Stage, string Context)>();
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .Callback((ILlmProvider _, string context, string _, McpSettingsData _, CancellationToken _, decimal? _) => contexts.Add(("generation", context)))
            .ReturnsAsync(new SqlGenerationResult(JoinSql, ["public.sales", "public.regions"]));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Callback((ILlmProvider _, string context, string _, McpSettingsData _, int _, decimal _, CancellationToken _) => contexts.Add(("candidates", context)))
            .ReturnsAsync([new SqlGenerationResult(JoinSql, ["public.sales", "public.regions"])]);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((ILlmProvider _, string _, string _, string _, string fullContext, string? _, string _, McpSettingsData _, string? generationContext, CancellationToken _) =>
            {
                contexts.Add(("repair", fullContext));
                contexts.Add(("repair-generation", generationContext!));
            })
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("relation does not exist");
        var settings = new McpSettingsData { EnableSelfConsistency = true, SelfConsistencyCandidateCount = 2, EnableSemanticLint = false };

        await CreatePipeline().RunAsync(
            Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        contexts.Select(x => x.Stage).Should().Equal("generation", "candidates", "repair", "repair-generation");
        contexts.Should().OnlyContain(x => x.Context == BoundedContext);
    }

    [Test]
    public async Task RunAsync_ARepairOverSqlReadingATableLeftOutOfTheContext_GetsThatTablesExactSchema()
    {
        string? repairTables = null;
        Generates(JoinSql);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), JoinSql, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((ILlmProvider _, string _, string _, string _, string _, string? tablesContext, string _, McpSettingsData _, string? _, CancellationToken _) => repairTables = tablesContext)
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("column does not exist");

        await CreatePipeline().RunAsync(
            Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData { EnableSemanticLint = false }, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        repairTables.Should().Be(RegionsSchema);
        _knowledgeGraph.Verify(x => x.GetTablesContextAsync(
            DataSourceId, ProjectId,
            It.Is<IEnumerable<string>>(y => y.Count() == 1 && y.Single().Equals("public.regions", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RunAsync_ARepairOverSqlReadingOnlyShownTables_GetsNoTableSchemas()
    {
        var repairCalled = false;
        string? repairTables = "unset";
        Generates(SalesSql);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), SalesSql, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((ILlmProvider _, string _, string _, string _, string _, string? tablesContext, string _, McpSettingsData _, string? _, CancellationToken _) =>
            {
                repairCalled = true;
                repairTables = tablesContext;
            })
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("column does not exist");

        await CreatePipeline().RunAsync(
            Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData { EnableSemanticLint = false }, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        repairCalled.Should().BeTrue();
        repairTables.Should().BeNull();
        _knowledgeGraph.Verify(x => x.GetTablesContextAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task RunAsync_AnEmptyResultRepairOverSqlReadingATableLeftOut_GetsThatTablesExactSchema_UnderTheContextsName()
    {
        const string quotedJoinSql = "SELECT r.name, SUM(s.amount) FROM \"public\".\"sales\" s JOIN \"public\".\"regions\" r ON r.id = s.region_id GROUP BY r.name";
        string? repairTables = null;
        Generates(quotedJoinSql);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), quotedJoinSql, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((ILlmProvider _, string _, string _, string _, string _, string? tablesContext, string _, McpSettingsData _, string? _, CancellationToken _) => repairTables = tablesContext)
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (0 rows)\n", null, 0, true));

        await CreatePipeline().RunAsync(
            Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData { EnableSemanticLint = false }, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        repairTables.Should().Be(RegionsSchema);
        _knowledgeGraph.Verify(x => x.GetTablesContextAsync(
            DataSourceId, ProjectId,
            It.Is<IEnumerable<string>>(y => y.SequenceEqual(new[] { "public.regions" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RunAsync_AnExecutionRepair_LooksUpALeftOutTableAlsoUnderTheContextsName()
    {
        IEnumerable<string>? lookedUp = null;
        Generates(JoinSql);
        _knowledgeGraph
            .Setup(x => x.GetTablesContextAsync(DataSourceId, ProjectId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback((int _, int _, IEnumerable<string> names, CancellationToken _) => lookedUp = names.ToList())
            .ReturnsAsync(RegionsSchema);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), JoinSql, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "column r.name does not exist", 0, false));

        await CreatePipeline().RunAsync(
            Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, Question, new McpSettingsData { EnableSemanticLint = false }, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        lookedUp.Should().NotBeNull();
        lookedUp.Should().Contain("public.regions");
    }

    private void Generates(string sql)
    {
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(sql, SqlTableNameExtractorNames(sql)));
    }

    private static List<string> SqlTableNameExtractorNames(string sql)
    {
        return Beacon.Core.Helpers.SqlTableNameExtractor.ExtractTableNames(sql);
    }

    private AskSqlPipeline CreatePipeline()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));

        return new AskSqlPipeline(
            _knowledgeGraph.Object,
            _sqlGeneration.Object,
            TestSqlGate.Create(guardrail.Object),
            TestRowSecurity.Resolver,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
            NullLogger<AskSqlPipeline>.Instance);
    }
}
