using System.Diagnostics;
using System.Diagnostics.Metrics;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Telemetry;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// One <c>beacon.llm.completion</c> span per completion through the delegating provider, tagged with the provider, the
/// configured model, the operation, token counts and the outcome, with <c>beacon.llm.calls</c> and <c>beacon.llm.tokens</c>
/// recorded alongside; the ask stages open <c>beacon.ask.*</c> spans tagged with counts. No tag carries the prompt, the
/// completion, SQL or the question.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LlmTelemetryTests
{
    // Const, never a static ActivitySource field: referencing that inside ShouldListenTo re-enters its static ctor.
    private const string TestSourceName = "Beacon.Tests.LlmTelemetry";
    private const string PromptMarker = "PROMPT-MARKER-5e1d";
    private const string SystemMarker = "SYSTEM-MARKER-9b2c";
    private const string CompletionMarker = "COMPLETION-MARKER-a41f";
    private const string Model = "claude-telemetry-test";
    private const int DataSourceId = 3;
    private const int ProjectId = 11;
    private const string Question = "QUESTION-MARKER-77c0 revenue by month";
    private const string GeneratedSql = "SELECT amount FROM public.sales";
    private const string RepairedSql = "SELECT total_amount FROM public.sales";
    private const string JoinSql = "SELECT r.name, SUM(s.amount) FROM public.sales s JOIN public.regions r ON r.id = s.region_id GROUP BY r.name";

    private static readonly ActivitySource TestSource = new(TestSourceName);

    private ActivityListener _listener = null!;
    private MeterListener _meterListener = null!;
    private List<Activity> _stopped = null!;
    private List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = null!;
    private string _operation = null!;

    [SetUp]
    public void SetUp()
    {
        _stopped = [];
        _measurements = [];

        // A per-test operation name isolates this fixture's measurements from any other completion in the process.
        _operation = "test.operation." + Guid.NewGuid().ToString("N");

        _listener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == TestSourceName || x.Name == BeaconTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = x =>
            {
                lock (_stopped)
                {
                    _stopped.Add(x);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BeaconTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagMap = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                tagMap[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, tagMap));
            }
        });
        _meterListener.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _meterListener.Dispose();
        _listener.Dispose();
    }

    [Test]
    public async Task Completion_ThroughTheDelegatingProvider_EmitsOneSpanWithTokenTagsAndNoContent()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = CompletionMarker,
                Model = Model,
                InputTokens = 120,
                OutputTokens = 30,
                CacheReadInputTokens = 900,
                CacheWriteInputTokens = 400
            });
        var delegating = Delegating(inner.Object);

        using (var parent = TestSource.StartActivity("test"))
        {
            await delegating.CompleteAsync(MarkedRequest());
            parent!.Stop();
            Spans(parent, BeaconTelemetry.LlmCompletionActivityName).Should().ContainSingle();
        }

        var span = _stopped.Single(x => x.OperationName == BeaconTelemetry.LlmCompletionActivityName && Equals(x.GetTagItem(BeaconTelemetry.LlmOperationTag), _operation));
        span.Kind.Should().Be(ActivityKind.Client);
        span.GetTagItem(BeaconTelemetry.LlmProviderTag).Should().Be("claude");
        span.GetTagItem(BeaconTelemetry.LlmRequestModelTag).Should().Be(Model);
        span.GetTagItem(BeaconTelemetry.LlmInputTokensTag).Should().Be(120);
        span.GetTagItem(BeaconTelemetry.LlmOutputTokensTag).Should().Be(30);
        span.GetTagItem(BeaconTelemetry.LlmCacheReadTokensTag).Should().Be(900);
        span.GetTagItem(BeaconTelemetry.LlmCacheWriteTokensTag).Should().Be(400);
        span.GetTagItem(BeaconTelemetry.LlmCacheHintTag).Should().Be(true);
        span.GetTagItem(BeaconTelemetry.LlmResponseModelTag).Should().Be(Model);
        span.GetTagItem(BeaconTelemetry.LlmTruncatedTag).Should().Be(false);
        span.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeSuccess);
        span.Status.Should().Be(ActivityStatusCode.Unset);
        AssertNoContent(span);
    }

    [Test]
    public async Task FastTierCompletion_NamesTheFastModelOnItsSpan()
    {
        const string fastModel = "claude-telemetry-fast";
        var main = new Mock<ILlmProvider>(MockBehavior.Strict);
        var fast = new Mock<ILlmProvider>();
        fast
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = CompletionMarker, Model = fastModel, InputTokens = 12, OutputTokens = 3 });
        var config = new LlmConfiguration { Provider = AiProvider.Claude, Model = Model, FastModel = fastModel };
        var factory = new LlmProviderFactory(() => config, [new FixedRegistration(main.Object, fast.Object)]);
        var manager = new LlmProviderManager(config, factory, NullLogger<LlmProviderManager>.Instance);
        var delegating = new DelegatingLlmProvider(manager, new LlmRequestQueue(1, 1));
        var request = MarkedRequest() with { Tier = LlmTier.Fast };

        await delegating.CompleteAsync(request);

        fast.Verify(x => x.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
        var span = _stopped.Single(x => x.OperationName == BeaconTelemetry.LlmCompletionActivityName && Equals(x.GetTagItem(BeaconTelemetry.LlmOperationTag), _operation));
        span.GetTagItem(BeaconTelemetry.LlmProviderTag).Should().Be("claude");
        span.GetTagItem(BeaconTelemetry.LlmRequestModelTag).Should().Be(fastModel);
        span.GetTagItem(BeaconTelemetry.LlmResponseModelTag).Should().Be(fastModel);
        span.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeSuccess);
        _measurements
            .Where(x => x.Instrument == BeaconTelemetry.LlmCalls.Name)
            .Where(x => Equals(x.Tags.GetValueOrDefault(BeaconTelemetry.LlmOperationTag), _operation))
            .Should().ContainSingle()
            .Which.Tags[BeaconTelemetry.LlmRequestModelTag].Should().Be(fastModel);
        AssertNoContent(span);
    }

    [Test]
    public async Task Completion_RecordsTheCallAndOneTokenMeasurementPerTokenType()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = CompletionMarker, Model = Model, InputTokens = 120, OutputTokens = 30, CacheReadInputTokens = 900, CacheWriteInputTokens = 400 });

        await Delegating(inner.Object).CompleteAsync(MarkedRequest());

        var mine = _measurements
            .Where(x => Equals(x.Tags.GetValueOrDefault(BeaconTelemetry.LlmOperationTag), _operation))
            .ToList();
        var call = mine.Should().ContainSingle(x => x.Instrument == BeaconTelemetry.LlmCallsInstrument).Subject;
        call.Value.Should().Be(1);
        call.Tags[BeaconTelemetry.OutcomeTag].Should().Be(BeaconTelemetry.OutcomeSuccess);
        call.Tags[BeaconTelemetry.LlmProviderTag].Should().Be("claude");
        call.Tags[BeaconTelemetry.LlmRequestModelTag].Should().Be(Model);
        mine
            .Where(x => x.Instrument == BeaconTelemetry.LlmTokensInstrument)
            .Select(x => (Type: x.Tags[BeaconTelemetry.LlmTokenTypeTag], x.Value))
            .Should()
            .BeEquivalentTo(new[]
            {
                (Type: (object?)BeaconTelemetry.TokenTypeInput, Value: 120L),
                (Type: (object?)BeaconTelemetry.TokenTypeOutput, Value: 30L),
                (Type: (object?)BeaconTelemetry.TokenTypeCacheRead, Value: 900L),
                (Type: (object?)BeaconTelemetry.TokenTypeCacheWrite, Value: 400L)
            });
        mine.SelectMany(x => x.Tags.Values).Should().NotContain(x => x is string && ((string)x!).Contains("MARKER"));
    }

    [Test]
    public async Task FailedCompletion_TagsTheOuterAndRootErrorTypesOnly()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("provider said " + PromptMarker, new HttpRequestException("upstream said " + PromptMarker)));

        var complete = () => Delegating(inner.Object).CompleteAsync(MarkedRequest());

        await complete.Should().ThrowAsync<AiServiceException>();
        var span = CompletionSpan();
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull();
        span.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeError);
        span.GetTagItem(BeaconTelemetry.ErrorTypeTag).Should().Be(nameof(AiServiceException));
        span.GetTagItem(BeaconTelemetry.ErrorRootTypeTag).Should().Be(nameof(HttpRequestException));
        AssertNoContent(span);
        var call = CallMeasurement();
        call.Tags[BeaconTelemetry.OutcomeTag].Should().Be(BeaconTelemetry.OutcomeError);
        call.Tags[BeaconTelemetry.ErrorTypeTag].Should().Be(nameof(AiServiceException));
    }

    [Test]
    public async Task CancelledCompletion_IsTaggedCancelled_NotAnError()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var complete = () => Delegating(inner.Object).CompleteAsync(MarkedRequest(), cancellation.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        var span = CompletionSpan();
        span.Status.Should().Be(ActivityStatusCode.Unset);
        span.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeCancelled);
        span.GetTagItem(BeaconTelemetry.ErrorTypeTag).Should().BeNull();
        span.GetTagItem(BeaconTelemetry.ErrorRootTypeTag).Should().BeNull();
        var call = CallMeasurement();
        call.Tags[BeaconTelemetry.OutcomeTag].Should().Be(BeaconTelemetry.OutcomeCancelled);
        call.Tags.Should().NotContainKey(BeaconTelemetry.ErrorTypeTag);
    }

    // The model a provider reports back is tagged only when it reads as a model name; the truncation flag always is.
    [Test]
    public async Task Completion_WithAResponseModelThatIsNotAModelName_LeavesItOffTheSpan()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = CompletionMarker, Model = "model " + PromptMarker, Truncated = true });

        await Delegating(inner.Object).CompleteAsync(MarkedRequest());

        var span = CompletionSpan();
        span.GetTagItem(BeaconTelemetry.LlmResponseModelTag).Should().BeNull();
        span.GetTagItem(BeaconTelemetry.LlmTruncatedTag).Should().Be(true);
        span.GetTagItem(BeaconTelemetry.LlmRequestModelTag).Should().Be(Model);
        AssertNoContent(span);
    }

    [Test]
    public async Task Completion_WithoutACacheBreakpoint_TagsNoCacheHint()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = CompletionMarker, Model = Model });

        await Delegating(inner.Object).CompleteAsync(MarkedRequest() with { Messages = [new ChatMessage(ConversationRole.User, PromptMarker)] });

        CompletionSpan().GetTagItem(BeaconTelemetry.LlmCacheHintTag).Should().Be(false);
    }

    [Test]
    public async Task Completion_WithNoMessages_TagsNoCacheHint()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = CompletionMarker, Model = Model });

        await Delegating(inner.Object).CompleteAsync(MarkedRequest() with { Messages = null! });

        CompletionSpan().GetTagItem(BeaconTelemetry.LlmCacheHintTag).Should().Be(false);
    }

    [Test]
    public async Task Completion_WithoutARequest_Throws()
    {
        var complete = () => Delegating(new Mock<ILlmProvider>().Object).CompleteAsync(null!);

        await complete.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task AskPipeline_EmitsGenerationRepairAndExecutionStageSpans_WithCountsOnly()
    {
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext { FullContext = "schema-context " + SystemMarker, DatabaseDialect = "PostgreSQL", TotalTableCount = 1 });
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["sales"]));
        sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RepairedSql);
        var executor = new Mock<IAskSqlExecutor>();
        executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "column \"amount\" does not exist", 0, false));
        executor
            .Setup(x => x.ExecuteAsync(DataSourceId, RepairedSql, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (12 rows)\n", null, 12, true));
        var pipeline = new AskSqlPipeline(
            knowledgeGraph.Object,
            sqlGeneration.Object,
            TestSqlGate.Create(guardrail.Object),
            TestRowSecurity.Resolver,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
            NullLogger<AskSqlPipeline>.Instance);

        using var parent = TestSource.StartActivity("test");
        await pipeline.RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, new McpSettingsData(), executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);
        parent!.Stop();

        var generation = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageGeneration).Should().ContainSingle().Subject;
        generation.GetTagItem(BeaconTelemetry.AskStageTag).Should().Be(BeaconTelemetry.AskStageGeneration);
        generation.GetTagItem(BeaconTelemetry.AskTablesTag).Should().Be(1);
        var repair = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageRepair).Should().ContainSingle().Subject;
        repair.GetTagItem(BeaconTelemetry.AskRepairTriggerTag).Should().Be("execution");
        repair.GetTagItem(BeaconTelemetry.AskProducedSqlTag).Should().Be(true);
        var executions = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageExecution);
        executions.Select(x => x.GetTagItem(BeaconTelemetry.AskStatementPurposeTag))
            .Should().Equal(McpStatementPurposes.Initial, McpStatementPurposes.Repair);
        executions.Select(x => x.GetTagItem(BeaconTelemetry.ResultRowsTag)).Should().Equal(0, 12);
        foreach (var span in Spans(parent))
        {
            AssertNoContent(span);
        }
    }

    [Test]
    public async Task AskPipeline_TagsTheGenerationStageSpan_WithTheContextSizeAndTableCounts()
    {
        var context = new SmartSchemaContext
        {
            FullContext = "schema-context " + SystemMarker,
            DatabaseDialect = "PostgreSQL",
            TotalTableCount = 6,
            TablesShown = 4,
            TablesNotShown = ["public.audit_log", "public.archive"]
        };
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["sales"]));

        using var parent = TestSource.StartActivity("test");
        await Pipeline(sqlGeneration, knowledgeGraph).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, new McpSettingsData(), SucceedingExecutor(rows: 1).Object, new AskSqlPipelineOptions(), CancellationToken.None);
        parent!.Stop();

        var generation = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageGeneration).Should().ContainSingle().Subject;
        generation.GetTagItem(BeaconTelemetry.AskContextCharsTag).Should().Be(context.FullContext.Length);
        generation.GetTagItem(BeaconTelemetry.AskTablesShownTag).Should().Be(4);
        generation.GetTagItem(BeaconTelemetry.AskTablesOmittedTag).Should().Be(2);
        AssertNoContent(generation);
    }

    [Test]
    public async Task AskPipeline_FailedGeneration_StillTagsTheGenerationStageSpanWithTheContextSize()
    {
        var context = new SmartSchemaContext
        {
            FullContext = "schema-context " + SystemMarker,
            DatabaseDialect = "PostgreSQL",
            TotalTableCount = 3,
            TablesShown = 2,
            TablesNotShown = ["public.archive"]
        };
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ThrowsAsync(new AiServiceException("context window exceeded"));

        using var parent = TestSource.StartActivity("test");
        var run = () => Pipeline(sqlGeneration, knowledgeGraph).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, new McpSettingsData(), SucceedingExecutor(rows: 1).Object, new AskSqlPipelineOptions(), CancellationToken.None);
        await run.Should().ThrowAsync<AiServiceException>();
        parent!.Stop();

        var generation = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageGeneration).Should().ContainSingle().Subject;
        generation.Status.Should().Be(ActivityStatusCode.Error);
        generation.GetTagItem(BeaconTelemetry.AskContextCharsTag).Should().Be(context.FullContext.Length);
        generation.GetTagItem(BeaconTelemetry.AskTablesShownTag).Should().Be(2);
        generation.GetTagItem(BeaconTelemetry.AskTablesOmittedTag).Should().Be(1);
        AssertNoContent(generation);
    }

    [Test]
    public async Task AskPipeline_EmitsTheSelfConsistencyStageSpan_WithTheCandidateCount()
    {
        var settings = new McpSettingsData { EnableSelfConsistency = true, SelfConsistencyCandidateCount = 3 };
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(JoinSql, ["sales", "regions"]));
        sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), 2, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SqlGenerationResult(JoinSql, ["sales", "regions"]), new SqlGenerationResult(JoinSql, ["sales", "regions"])]);
        var executor = SucceedingExecutor(rows: 4);

        using var parent = TestSource.StartActivity("test");
        await Pipeline(sqlGeneration).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, settings, executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);
        parent!.Stop();

        var vote = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageSelfConsistency).Should().ContainSingle().Subject;
        vote.GetTagItem(BeaconTelemetry.AskStageTag).Should().Be(BeaconTelemetry.AskStageSelfConsistency);
        vote.GetTagItem(BeaconTelemetry.AskCandidatesTag).Should().Be(3);
        vote.Status.Should().Be(ActivityStatusCode.Unset);
        Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageExecution)
            .Select(x => x.GetTagItem(BeaconTelemetry.AskStatementPurposeTag))
            .Should().Contain(McpStatementPurposes.Candidate);
        foreach (var span in Spans(parent))
        {
            AssertNoContent(span);
        }
    }

    // A failing vote falls back to the single candidate; its stage span still records the failure, by type only.
    [Test]
    public async Task AskPipeline_FailingSelfConsistency_MarksItsStageSpanFailed()
    {
        var settings = new McpSettingsData { EnableSelfConsistency = true, SelfConsistencyCandidateCount = 3 };
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(JoinSql, ["sales", "regions"]));
        sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vote failed " + PromptMarker));

        using var parent = TestSource.StartActivity("test");
        await Pipeline(sqlGeneration).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, settings, SucceedingExecutor(rows: 4).Object, new AskSqlPipelineOptions(), CancellationToken.None);
        parent!.Stop();

        var vote = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageSelfConsistency).Should().ContainSingle().Subject;
        vote.Status.Should().Be(ActivityStatusCode.Error);
        vote.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeError);
        vote.GetTagItem(BeaconTelemetry.ErrorTypeTag).Should().Be(nameof(InvalidOperationException));
        AssertNoContent(vote);
    }

    // Each repair trigger opens a repair stage span tagged with it; a repair that brings no statement back says so.
    [TestCase("schema")]
    [TestCase("lint")]
    [TestCase("dry-run")]
    [TestCase("execution")]
    [TestCase("empty-result")]
    public async Task AskPipeline_RepairStageSpan_IsTaggedWithItsTrigger_AndProducedSqlFalseWithoutAStatement(string trigger)
    {
        var generated = trigger switch
        {
            "schema" => "SELECT bogus FROM orders",
            "lint" => "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o",
            _ => GeneratedSql
        };
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "schema-context " + SystemMarker,
                DatabaseDialect = "PostgreSQL",
                TotalTableCount = 1,
                SchemaCatalog = trigger == "schema"
                    ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { ["orders"] = new(StringComparer.OrdinalIgnoreCase) { "id" } }
                    : []
            });
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(generated, ["orders"]));
        sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var executor = SucceedingExecutor(rows: trigger == "empty-result" ? 0 : 1);
        if (trigger == "dry-run")
        {
            executor
                .Setup(x => x.DryRunAsync(DataSourceId, generated, It.IsAny<CancellationToken>()))
                .ReturnsAsync("syntax error at or near \"FROM\"");
        }

        if (trigger == "execution")
        {
            executor
                .Setup(x => x.ExecuteAsync(DataSourceId, generated, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AskExecutionResult(null, "column \"amount\" does not exist", 0, false));
        }

        using var parent = TestSource.StartActivity("test");
        await Pipeline(sqlGeneration, knowledgeGraph).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, new McpSettingsData(), executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);
        parent!.Stop();

        var repairs = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageRepair);
        repairs.Should().NotBeEmpty();
        repairs[0].GetTagItem(BeaconTelemetry.AskStageTag).Should().Be(BeaconTelemetry.AskStageRepair);
        repairs[0].GetTagItem(BeaconTelemetry.AskRepairTriggerTag).Should().Be(trigger);
        repairs.Should().OnlyContain(x => Equals(x.GetTagItem(BeaconTelemetry.AskProducedSqlTag), false));
        foreach (var span in Spans(parent))
        {
            AssertNoContent(span);
        }
    }

    [Test]
    public async Task AskPipeline_ThrowingExecution_MarksTheExecutionStageSpanFailed_ByTypeOnly()
    {
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["sales"]));
        var executor = SucceedingExecutor(rows: 1);
        executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("database said " + PromptMarker, new TimeoutException("timed out " + PromptMarker)));

        using var parent = TestSource.StartActivity("test");
        var run = () => Pipeline(sqlGeneration).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, new McpSettingsData(), executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);
        await run.Should().ThrowAsync<AiServiceException>();
        parent!.Stop();

        var execution = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageExecution).Should().ContainSingle().Subject;
        execution.Status.Should().Be(ActivityStatusCode.Error);
        execution.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeError);
        execution.GetTagItem(BeaconTelemetry.ErrorTypeTag).Should().Be(nameof(AiServiceException));
        execution.GetTagItem(BeaconTelemetry.ErrorRootTypeTag).Should().Be(nameof(TimeoutException));
        AssertNoContent(execution);
    }

    [Test]
    public async Task AskPipeline_CancelledGeneration_MarksTheGenerationStageSpanCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var sqlGeneration = new Mock<ISqlGenerationService>();
        sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        using var parent = TestSource.StartActivity("test");
        var run = () => Pipeline(sqlGeneration).RunAsync(
            new Mock<ILlmProvider>().Object, DataSourceId, ProjectId, Question, new McpSettingsData(), SucceedingExecutor(rows: 1).Object, new AskSqlPipelineOptions(), cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        parent!.Stop();

        var generation = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageGeneration).Should().ContainSingle().Subject;
        generation.Status.Should().Be(ActivityStatusCode.Unset);
        generation.GetTagItem(BeaconTelemetry.OutcomeTag).Should().Be(BeaconTelemetry.OutcomeCancelled);
        generation.GetTagItem(BeaconTelemetry.ErrorTypeTag).Should().BeNull();
    }

    [Test]
    public async Task ThrowingIntentAndRouting_MarkTheirStageSpansFailed()
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("provider said " + PromptMarker));
        var sources = new List<DataSourceKnowledge> { new() { DataSourceId = DataSourceId, Name = "Sales" }, new() { DataSourceId = DataSourceId + 1, Name = "Hr" } };

        using var parent = TestSource.StartActivity("test");
        var classify = () => new IntentClassifier().ClassifyAsync(provider.Object, Question, CancellationToken.None);
        var route = () => new DataSourceRouter(NullLogger<DataSourceRouter>.Instance).RouteAsync(provider.Object, sources, Question, CancellationToken.None);
        await classify.Should().ThrowAsync<AiServiceException>();
        await route.Should().ThrowAsync<AiServiceException>();
        parent!.Stop();

        foreach (var stage in new[] { BeaconTelemetry.AskStageIntent, BeaconTelemetry.AskStageRouting })
        {
            var span = Spans(parent, BeaconTelemetry.AskStageActivityPrefix + stage).Should().ContainSingle().Subject;
            span.Status.Should().Be(ActivityStatusCode.Error);
            span.GetTagItem(BeaconTelemetry.ErrorTypeTag).Should().Be(nameof(AiServiceException));
            span.GetTagItem(BeaconTelemetry.ErrorRootTypeTag).Should().Be(nameof(AiServiceException));
            AssertNoContent(span);
        }
    }

    [Test]
    public async Task IntentAndRouting_EmitTheirStageSpans()
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "DATA_QUERY" });
        var sources = new List<DataSourceKnowledge> { new() { DataSourceId = DataSourceId, Name = "Sales" } };

        using var parent = TestSource.StartActivity("test");
        await new IntentClassifier().ClassifyAsync(provider.Object, Question, CancellationToken.None);
        await new DataSourceRouter(NullLogger<DataSourceRouter>.Instance).RouteAsync(provider.Object, sources, Question, CancellationToken.None);
        parent!.Stop();

        Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageIntent)
            .Should().ContainSingle()
            .Which.GetTagItem(BeaconTelemetry.AskIntentTag).Should().Be(nameof(IntentClassification.DataQuery));
        Spans(parent, BeaconTelemetry.AskStageActivityPrefix + BeaconTelemetry.AskStageRouting)
            .Should().ContainSingle()
            .Which.GetTagItem(BeaconTelemetry.AskSourcesTag).Should().Be(1);
        provider.Verify(x => x.CompleteAsync(It.Is<LlmRequest>(y => y.Operation == LlmOperations.AskIntent), It.IsAny<CancellationToken>()), Times.Once);
    }

    private Activity CompletionSpan()
    {
        lock (_stopped)
        {
            return _stopped.Single(x => x.OperationName == BeaconTelemetry.LlmCompletionActivityName && Equals(x.GetTagItem(BeaconTelemetry.LlmOperationTag), _operation));
        }
    }

    private (string Instrument, long Value, Dictionary<string, object?> Tags) CallMeasurement()
    {
        lock (_measurements)
        {
            return _measurements.Single(x => x.Instrument == BeaconTelemetry.LlmCallsInstrument && Equals(x.Tags.GetValueOrDefault(BeaconTelemetry.LlmOperationTag), _operation));
        }
    }

    private static AskSqlPipeline Pipeline(Mock<ISqlGenerationService> sqlGeneration, Mock<IKnowledgeGraphService>? knowledgeGraph = null)
    {
        if (knowledgeGraph == null)
        {
            knowledgeGraph = new Mock<IKnowledgeGraphService>();
            knowledgeGraph
                .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SmartSchemaContext { FullContext = "schema-context " + SystemMarker, DatabaseDialect = "PostgreSQL", TotalTableCount = 2 });
        }

        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));

        return new AskSqlPipeline(
            knowledgeGraph.Object,
            sqlGeneration.Object,
            TestSqlGate.Create(guardrail.Object),
            TestRowSecurity.Resolver,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
            NullLogger<AskSqlPipeline>.Instance);
    }

    private static Mock<IAskSqlExecutor> SucceedingExecutor(int rows)
    {
        var executor = new Mock<IAskSqlExecutor>();
        executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult($"### Results ({rows} rows)\n", null, rows, true));

        return executor;
    }

    private LlmRequest MarkedRequest()
    {
        return new LlmRequest
        {
            SystemPrompt = SystemMarker,
            Messages = [new ChatMessage(ConversationRole.User, PromptMarker) { CacheBreakpoint = true }],
            Operation = _operation
        };
    }

    private List<Activity> Spans(Activity parent, string? name = null)
    {
        lock (_stopped)
        {
            return _stopped
                .Where(x => x.TraceId == parent.TraceId)
                .Where(x => x != parent)
                .Where(x => name == null || x.OperationName == name)
                .OrderBy(x => x.StartTimeUtc)
                .ToList();
        }
    }

    private static void AssertNoContent(Activity span)
    {
        foreach (var tag in span.TagObjects)
        {
            var value = tag.Value?.ToString() ?? string.Empty;
            value.Should().NotContain(PromptMarker, "tag {0} must not carry the prompt", tag.Key);
            value.Should().NotContain(SystemMarker, "tag {0} must not carry the system prompt", tag.Key);
            value.Should().NotContain(CompletionMarker, "tag {0} must not carry the completion", tag.Key);
            value.Should().NotContain("QUESTION-MARKER", "tag {0} must not carry the question", tag.Key);
            value.Should().NotContain("SELECT", "tag {0} must not carry SQL", tag.Key);
        }
    }

    private static DelegatingLlmProvider Delegating(ILlmProvider inner)
    {
        var config = new LlmConfiguration { Provider = AiProvider.Claude, Model = Model };
        var factory = new LlmProviderFactory(() => config, [new FixedRegistration(inner)]);
        var manager = new LlmProviderManager(config, factory, NullLogger<LlmProviderManager>.Instance);

        return new DelegatingLlmProvider(manager, new LlmRequestQueue(1, 1));
    }

    private sealed class FixedRegistration(ILlmProvider provider, ILlmProvider? fastProvider = null) : ILlmProviderRegistration
    {
        public AiProvider Provider => AiProvider.Claude;

        public bool RequiresApiKey => false;

        public ILlmProvider Create(LlmConfiguration configuration, bool fast) => fast ? fastProvider ?? provider : provider;
    }
}
