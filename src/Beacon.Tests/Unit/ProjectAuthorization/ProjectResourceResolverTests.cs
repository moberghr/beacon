using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// What the resolver reads for queries and runs: a query, and the subscriptions and tasks on it, carry the query's
/// owner, who alone (with Admins) reaches them while the query has no steps; a run is reached by the sources it
/// recorded, or for a run recorded before Beacon kept them by its live query's steps; a run with no source to go by is
/// an Admin's. Subscription 100's query reads data source 10, 200's has no steps, 300's query is archived.
/// </summary>
[TestFixture]
public class ProjectResourceResolverTests
{
    private const string Owner = "ext-owner";

    [Test]
    public async Task AQueryAndTheWorkOnIt_CarryTheQuerysOwner()
    {
        var query = await Resolve(ProjectResource.Query(200));
        var subscription = await Resolve(ProjectResource.Subscription(200));
        var task = await Resolve(ProjectResource.Task(1));

        query.SourceSets.Should().ContainSingle().Which.Should().BeEmpty();
        query.QueryOwnerUserId.Should().Be(Owner);
        subscription.QueryOwnerUserId.Should().Be(Owner);
        subscription.OwnerUserId.Should().Be("ext-subscriber", "the subscription's own owner stays its recorded owner");
        task.QueryOwnerUserId.Should().Be(Owner);
    }

    [Test]
    public async Task ARunWithRecordedSources_IsReachedByThem()
    {
        var run = await Resolve(ProjectResource.Run(1));

        run.SourceSets.Should().ContainSingle().Which.Should().Equal(10);
        run.IsLegacyRun.Should().BeFalse();
    }

    [Test]
    public async Task ALegacyRun_IsReachedByItsLiveQuerysSteps()
    {
        var run = await Resolve(ProjectResource.Run(2));

        run.SourceSets.Should().ContainSingle().Which.Should().Equal(10);
        run.IsLegacyRun.Should().BeTrue();
        run.IsAdminOnly.Should().BeFalse();
    }

    [TestCase(3, TestName = "{m}(recorded no source)")]
    [TestCase(4, TestName = "{m}(legacy, query without steps)")]
    [TestCase(5, TestName = "{m}(legacy, archived query)")]
    public async Task ARunWithNoSourceToGoBy_IsAnAdminsAlone(int runId)
    {
        var run = await Resolve(ProjectResource.Run(runId));

        run.IsAdminOnly.Should().BeTrue();
        run.SourceSets.Should().BeEmpty();
    }

    private static async Task<ResolvedProjectResource> Resolve(ProjectResource resource)
    {
        var withSource = Subscription(100, archivedQuery: false, 10);
        var withoutSteps = Subscription(200, archivedQuery: false);
        var onArchivedQuery = Subscription(300, archivedQuery: true, 10);
        var subscriptions = new List<Subscription> { withSource, withoutSteps, onArchivedQuery };
        var runs = new List<QueryExecutionHistory>
        {
            Run(1, 100, [10]),
            Run(2, 100, null),
            Run(3, 100, []),
            Run(4, 200, null),
            Run(5, 300, null)
        };
        var tasks = new List<QueryTask> { new() { Id = 1, SubscriptionId = 200, LatestResultCount = 0, Subscription = withoutSteps } };
        await using var context = new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(Query)] = RecordingBeaconContext.MemorySet(subscriptions.Select(x => x.Query).ToList(), []),
                [typeof(Subscription)] = RecordingBeaconContext.MemorySet(subscriptions, []),
                [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(runs, []),
                [typeof(QueryTask)] = RecordingBeaconContext.MemorySet(tasks, [])
            },
            []);

        return await new ProjectResourceResolver().ResolveAsync(context, resource, CancellationToken.None);
    }

    private static Subscription Subscription(int id, bool archivedQuery, params int[] stepSources)
    {
        return new Subscription
        {
            Id = id,
            QueryId = id,
            CronExpression = "0 8 * * *",
            OwnerUserId = "ext-subscriber",
            Query = new Query
            {
                Id = id,
                Name = $"query {id}",
                OwnerUserId = Owner,
                ArchivedTime = archivedQuery ? DateTime.UtcNow : null,
                Steps = stepSources
                    .Select((x, i) => new QueryStep { QueryId = id, DataSourceId = x, StepOrder = i + 1, SqlValue = "SELECT 1" })
                    .ToList()
            }
        };
    }

    private static QueryExecutionHistory Run(int id, int subscriptionId, int[]? dataSourceIds)
    {
        return new QueryExecutionHistory
        {
            Id = id,
            SubscriptionId = subscriptionId,
            ResultCount = 0,
            CompiledSql = "SELECT 1",
            NotificationStatus = NotificationStatus.NotificationSent,
            ExecutionTimeMs = 1,
            DataSourceIds = dataSourceIds
        };
    }
}
