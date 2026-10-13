using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// The learning aggregation stores a pattern's example SQL only as the grounding SQL check returns it (without comments):
/// SQL the check refuses is not stored — a new pattern gets none, an existing one keeps the example it has — and is counted
/// for the run and logged by identifiers only (§1.11). Drives the detectors over in-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class LearnedPatternExampleSqlTests
{
    private static readonly IReadOnlySet<int> NoCurators = new HashSet<int>();

    private const int ProjectId = 1;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;

    [Test]
    public async Task AMinedExampleSql_IsStoredWithoutComments()
    {
        var (service, context, saved, _) = Build(TestGroundingSqlChecks.Real());
        var stats = new McpLearningAggregationService.ExtractionStats();

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals("SELECT id FROM orders -- the usual one"), NoCurators, stats, CancellationToken.None);

        saved.OfType<McpLearnedPattern>().Should().ContainSingle().Which.ExampleSql.Should().Be("SELECT id FROM orders");
        stats.BlockedExampleSql.Should().Be(0);
    }

    [Test]
    public async Task AMinedExampleSqlTheCheckRefuses_IsStoredAsNull_CountedAndLoggedByIdsOnly()
    {
        const string refusedSql = "SELECT secret_column FROM orders";
        var (service, context, saved, logs) = Build(TestGroundingSqlChecks.Real());
        var stats = new McpLearningAggregationService.ExtractionStats();

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(refusedSql), NoCurators, stats, CancellationToken.None);

        var pattern = saved.OfType<McpLearnedPattern>().Should().ContainSingle().Subject;
        pattern.ExampleSql.Should().BeNull();
        pattern.ExampleQuestion.Should().Be("orders last week", "only the SQL is dropped");
        stats.BlockedExampleSql.Should().Be(1);
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Be($"The example SQL of a CommonQuery pattern for project {ProjectId}, data source {DataSourceId} did not pass the SQL check and was not stored");
        logs.Contains("secret_column").Should().BeFalse("no SQL or refusal text reaches the log");
    }

    [Test]
    public async Task AnExistingPattern_KeepsItsExample_WhenTheNewOneIsRefused()
    {
        var existing = new McpLearnedPattern
        {
            Id = 4,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "customers",
            PatternType = McpPatternType.JoinPattern,
            PatternContent = "Tables public.customers + public.orders are frequently joined together (2 queries)",
            ExampleSql = "SELECT o.id FROM orders o",
            SignalCount = 2,
            Status = McpPatternStatus.NeedsEvidence
        };
        var (service, context, saved, _) = Build(TestGroundingSqlChecks.Refusing().Object, existing);
        var stats = new McpLearningAggregationService.ExtractionStats();
        var signals = Enumerable.Range(0, 3)
            .Select(_ =>
                new McpQuerySignal
                {
                    Tool = "ask",
                    Question = "orders with their customers",
                    ProjectId = ProjectId,
                    DataSourceId = DataSourceId,
                    GeneratedSql = "SELECT * FROM public.orders o JOIN public.customers c ON c.id = o.customer_id",
                    TablesUsed = "[\"public.orders\",\"public.customers\"]",
                    IsSuccessful = true
                })
            .ToList();

        await service.DetectJoinPatternsAsync(context, ProjectId, DataSourceId, signals, retainContent: true, NoCurators, stats, CancellationToken.None);

        saved.Should().BeEmpty();
        existing.ExampleSql.Should().Be("SELECT o.id FROM orders o");
        existing.SignalCount.Should().Be(3, "the rest of the refresh still lands");
        stats.BlockedExampleSql.Should().Be(1);
    }

    private static List<McpQuerySignal> Signals(string sql)
    {
        return Enumerable.Range(0, 3)
            .Select(_ =>
                new McpQuerySignal
                {
                    Tool = "ask",
                    Question = "orders last week",
                    ProjectId = ProjectId,
                    DataSourceId = DataSourceId,
                    GeneratedSql = sql,
                    TablesUsed = "[\"public.orders\"]",
                    IsSuccessful = true
                })
            .ToList();
    }

    private static (McpLearningAggregationService Service, BeaconContext Context, List<object> Saved, LogRecorder Logs) Build(
        IGroundingSqlCheck check,
        McpLearnedPattern? existing = null)
    {
        var saved = new List<object>();
        var context = new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(existing == null ? new List<McpLearnedPattern>() : [existing], saved)
            },
            saved);
        var logs = new LogRecorder();
        var service = new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            check,
            logs.For<McpLearningAggregationService>());

        return (service, context, saved, logs);
    }
}
