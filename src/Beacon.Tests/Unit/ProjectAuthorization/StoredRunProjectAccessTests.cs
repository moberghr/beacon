using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Stored runs under project access: a run is readable through a project the caller reads that holds every data
/// source it recorded, or, when the caller reaches the openly reachable sources, when all of them are; a run recorded
/// before Beacon kept its sources is read by its subscription's live query the same way, and never by a caller
/// confined to a project list; a run with no data source to go by is an Admin's. Data source 10 is in project 1, 20
/// only in a restricted project, 40 openly reachable. Runs 1-3 recorded their sources, 4-6 are legacy, 7 recorded
/// none, 8 is legacy on an archived query.
/// </summary>
[TestFixture]
public class StoredRunProjectAccessTests
{
    [Test]
    public void ACallerReachingOpenSources_ReadsRunsThroughItsProjectsAndTheOpenSources_AndLegacyRunsByTheirQuery()
    {
        var readable = Readable(StoredRunScope.From(new ProjectSet([1], true, [20])));

        readable.Should().BeEquivalentTo([1, 3, 4], "runs 2 and 5 read the restricted-only source 20, and 6, 7 and 8 have no source to go by");
    }

    [Test]
    public void ACallerConfinedToAProjectList_ReadsOnlyRecordedRunsItsProjectsHold()
    {
        var readable = Readable(StoredRunScope.From(new ProjectSet([1], false, []) { IsConfinedToList = true }));

        readable.Should().BeEquivalentTo([1]);
    }

    [Test]
    public void AMemberWhenMembershipIsRequired_ReadsLegacyRunsByTheirQuerysCurrentSources()
    {
        var readable = Readable(StoredRunScope.From(new ProjectSet([1], false, [])));

        readable.Should().BeEquivalentTo([1, 4], "no source is openly reachable, but the caller carries no project list");
    }

    [Test]
    public void AnUnrestrictedCaller_ReadsEveryRun()
    {
        var readable = Readable(StoredRunScope.From(ProjectSet.All));

        readable.Should().BeEquivalentTo([1, 2, 3, 4, 5, 6, 7, 8]);
        StoredRunScope.From(ProjectSet.All).Should().BeSameAs(StoredRunScope.Unrestricted);
    }

    private static List<int> Readable(StoredRunScope scope)
    {
        var withSource10 = Subscription(100, 10);
        var withSource20 = Subscription(200, 20);
        var withoutSteps = Subscription(300);
        var onArchivedQuery = Subscription(400, 10);
        onArchivedQuery.Query.ArchivedTime = DateTime.UtcNow;
        var runs = new List<QueryExecutionHistory>
        {
            Run(1, withSource10.Id, [10]),
            Run(2, withSource20.Id, [20]),
            Run(3, withSource10.Id, [40]),
            Run(4, withSource10.Id, null),
            Run(5, withSource20.Id, null),
            Run(6, withoutSteps.Id, null),
            Run(7, withSource10.Id, []),
            Run(8, onArchivedQuery.Id, null)
        };
        using var context = new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(new List<ProjectDataSource>
                {
                    new() { ProjectId = 1, DataSourceId = 10 },
                    new() { ProjectId = 2, DataSourceId = 20 }
                }, []),
                [typeof(Subscription)] = RecordingBeaconContext.MemorySet(new List<Subscription> { withSource10, withSource20, withoutSteps, onArchivedQuery }, [])
            },
            []);

        return runs
            .AsQueryable()
            .WhereReadableWithin(context, scope)
            .Select(x => x.Id)
            .ToList();
    }

    private static Subscription Subscription(int id, params int[] stepSources)
    {
        return new Subscription
        {
            Id = id,
            QueryId = id,
            CronExpression = "0 8 * * *",
            Query = new Query
            {
                Id = id,
                Name = $"query {id}",
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
