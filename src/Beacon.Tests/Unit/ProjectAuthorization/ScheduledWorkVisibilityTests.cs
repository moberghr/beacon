using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// What readers see of scheduled work that runs as its owner: a subscription whose last run was skipped because its owner
/// may no longer run it says so (Control Tower and the subscription's detail), and the owners of data contracts, tasks'
/// AI actors and AI actors are named by display name or a neutral "User {id}" label — never by user name or external id.
/// No database (§4.7): SQL is captured and answered from a script, or rows are served from memory.
/// </summary>
[TestFixture]
public class ScheduledWorkVisibilityTests
{
    [Test]
    public async Task TheControlTower_FlagsASubscriptionWhoseLastRunWasSkipped()
    {
        var created = DateTime.UtcNow.AddDays(-10);
        var capture = new SqlCapture()
            .AnsweringCountsWithOne()
            .ThenScalar(2)
            .ThenRows(
                [7, created, "Late payments", DBNull.Value, false, false, DBNull.Value, DBNull.Value, 3, 2, 0, 0],
                [8, created, "Daily orders", DBNull.Value, false, false, DBNull.Value, DBNull.Value, 3, 3, 0, 0],
                [9, created, "Refunds", DBNull.Value, false, false, DBNull.Value, DBNull.Value, 3, 2, 0, 0])
            .ThenRows(
                [7, (int)NotificationStatus.Skipped, DateTime.UtcNow.AddHours(-1), 0, NotificationFailureReasons.OwnerNotAuthorized, 1],
                [8, (int)NotificationStatus.NotificationSent, DateTime.UtcNow.AddHours(-1), 4, DBNull.Value, 1],
                [9, (int)NotificationStatus.Skipped, DateTime.UtcNow.AddHours(-1), 0, NotificationFailureReasons.NotApproved, 1]);
        var service = new ControlTowerService(capture.Factory(), new MemoryCache(new MemoryCacheOptions()));

        var page = await service.GetSubscriptionHealthOverview(new GetControlTowerDataRequest { Readable = ProjectSet.All }, CancellationToken.None);

        page.Items.Should().HaveCount(3);
        page.Items.Single(x => x.SubscriptionId == 7).OwnerNotAuthorized.Should().BeTrue();
        page.Items.Single(x => x.SubscriptionId == 7).LastExecutionStatus.Should().Be(NotificationStatus.Skipped);
        page.Items.Single(x => x.SubscriptionId == 8).OwnerNotAuthorized.Should().BeFalse();
        page.Items.Single(x => x.SubscriptionId == 9).OwnerNotAuthorized.Should().BeFalse("a run skipped for an unapproved change is not about its owner");
        page.Items.Single(x => x.SubscriptionId == 9).LastExecutionStatus.Should().Be(NotificationStatus.Skipped);
        page.Items.Where(x => x.NotApproved).Select(x => x.SubscriptionId).Should().Equal([9], "only the run skipped for an unapproved change says so");
    }

    [Test]
    public async Task TheHomeActivityFeed_ShowsAReadableSkippedRun_InAWarnTone()
    {
        // The caller reads project 1 (source 10), not project 2 (source 20).
        var query = new Query { Id = 100, Name = "Late payments" };
        var subscription = new Subscription { Id = 50, QueryId = query.Id, Query = query, CronExpression = "0 * * * *" };
        var runs = new List<QueryExecutionHistory>
        {
            SkippedRun(1, subscription, 10),
            SkippedRun(2, subscription, 20)
        };
        var links = new List<ProjectDataSource>
        {
            new() { ProjectId = 1, DataSourceId = 10 },
            new() { ProjectId = 2, DataSourceId = 20 }
        };
        var factory = MemoryFactory(new Dictionary<Type, object>
        {
            [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(runs, []),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, []),
            [typeof(Subscription)] = RecordingBeaconContext.MemorySet(new List<Subscription> { subscription }, []),
            [typeof(QueryTask)] = RecordingBeaconContext.MemorySet(new List<QueryTask>(), []),
            [typeof(AnomalyEvent)] = RecordingBeaconContext.MemorySet(new List<AnomalyEvent>(), [])
        });

        var answer = await new GetHomeActivityHandler(factory, TestProjectAccess.Evaluator(TestProjectAccess.ReadableIn(1)))
            .Handle(new GetHomeActivityQuery(50), CancellationToken.None);

        answer.Items.Should().ContainSingle(x => x.Title == "Late payments was skipped")
            .Which.Should().BeEquivalentTo(new { Tone = "warn", Icon = "Alert", Meta = NotificationFailureReasons.OwnerNotAuthorized, Timestamp = runs[0].CreatedTime });
    }

    [Test]
    public async Task ASubscriptionsLastRunSkipped_Translates()
    {
        var database = new CommandCapture();
        var service = new SubscriptionService(database.NpgsqlFactory(), Mock.Of<IBeaconScheduler>(), Mock.Of<IAnomalyDetectionService>(), TestProjectAccess.Actors(), new BeaconConfiguration(), TestQueryServices.QueryOptions, new RecordingSecurityAudit(), TimeProvider.System);

        var act = () => service.GetSubscriptionDetails(5, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("the fake database returns no row");
        var sql = database.Commands.Should().ContainSingle().Subject.Text;
        sql.Should().MatchRegex(@"EXISTS \(\s+SELECT 1\s+FROM \(\s+SELECT q\d*\.comment, q\d*\.notification_status\s+FROM beacon\.query_execution_history AS q\d*\s+WHERE s\.id = q\d*\.subscription_id\s+ORDER BY q\d*\.created_time DESC\s+LIMIT 1\s+\) AS q\d*\s+WHERE q\d*\.notification_status = 8 AND \(q\d*\.comment <> 'Skipped: query change not approved\. Save the query to submit its SQL for approval, or restore a version and approve it\.' OR q\d*\.comment IS NULL\)\)", "the last run decides, a skipped run is stored as status 8, and a run skipped for an unapproved change is not about the owner");
        sql.Should().MatchRegex(@"WHERE q\d*\.notification_status = 8 AND q\d*\.comment = 'Skipped: query change not approved\.", "the last run skipped for an unapproved change is said apart");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ASubscriptionsDetail_SaysWhetherItsLastRunWasSkipped(bool skipped)
    {
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(x => x.GetSubscriptionDetails(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionDetailsData
            {
                SubscriptionId = 5,
                QueryName = "daily",
                Status = "Active",
                OwnerUserId = "ext-ada",
                OwnerDisplayName = "Ada Lovelace",
                OwnerNotAuthorized = skipped,
                NotApproved = !skipped
            });

        var result = await new GetSubscriptionDetailHandler(service.Object, TestChangeApprovals.Deliveries()).Handle(new GetSubscriptionDetailQuery(5), CancellationToken.None);

        result.Detail.OwnerNotAuthorized.Should().Be(skipped);
        result.Detail.NotApproved.Should().Be(!skipped);
    }

    [Test]
    public async Task ATasksAiActorCreator_IsNamedByDisplayNameOrALabel_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        await new GetTaskDetailHandler(capture.Factory(), null!, null!).Read(3, null, CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().MatchRegex(@"a\d*\.created_by_user_id IS NOT NULL AS c\d+", "only whether the actor has a creator is read");
        sql.Should().MatchRegex(@"WHEN u\d*\.display_name IS NULL OR btrim\(u\d*\.display_name, E' \\t\\n\\r'\) = '' THEN 'User ' \|\| u\d*\.id::text\s+ELSE u\d*\.display_name\s+END\s+FROM beacon\.users AS u\d*\s+WHERE u\d*\.archived_time IS NULL AND u\d*\.external_id = a\d*\.created_by_user_id", "a blank display name is labelled by id too");
        sql.Should().NotMatchRegex(@"user_name\)\s+FROM beacon\.users AS u\d*\s+WHERE u\d*\.archived_time IS NULL AND u\d*\.external_id = a\d*\.created_by_user_id", "the creator is never named by user name");
        sql.Split('\n')[0].Should().NotContain("created_by_user_id", "the creator's external id is not among the columns read");
    }

    [TestCase(null, "User 5")]
    [TestCase("Ada Lovelace", "Ada Lovelace")]
    public async Task AnAiActorsCreator_IsNamedByDisplayNameOrALabel_NeverByExternalId(string? displayName, string expected)
    {
        var handler = ActorDetails(new BeaconUser { Id = 5, ExternalId = "ext-ada", UserName = "ada@example.com", DisplayName = displayName }, "ext-ada");

        var result = await handler.Handle(new GetAiActorDetailsQuery { ActorId = 9 }, CancellationToken.None);

        result!.HasOwner.Should().BeTrue();
        result.OwnerDisplayName.Should().Be(expected);
    }

    [Test]
    public async Task AnAiActorWithoutACreator_HasNoOwner()
    {
        var handler = ActorDetails(new BeaconUser { Id = 5, ExternalId = "ext-ada", UserName = "ada" }, null);

        var result = await handler.Handle(new GetAiActorDetailsQuery { ActorId = 9 }, CancellationToken.None);

        result!.HasOwner.Should().BeFalse();
        result.OwnerDisplayName.Should().BeNull();
    }

    private static QueryExecutionHistory SkippedRun(int id, Subscription subscription, int dataSourceId)
    {
        return new QueryExecutionHistory
        {
            Id = id,
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            ResultCount = 0,
            CompiledSql = string.Empty,
            ExecutionTimeMs = 0,
            NotificationStatus = NotificationStatus.Skipped,
            Comment = NotificationFailureReasons.OwnerNotAuthorized,
            DataSourceIds = [dataSourceId],
            CreatedTime = DateTime.UtcNow.AddHours(-id)
        };
    }

    private static IDbContextFactory<BeaconContext> MemoryFactory(Dictionary<Type, object> sets)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, []));

        return factory.Object;
    }

    private static GetAiActorDetailsHandler ActorDetails(BeaconUser user, string? createdByUserId)
    {
        var actors = new Mock<IAiActorServiceExtended>();
        actors
            .Setup(x => x.GetActorAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorEntity { Id = 9, Name = "Watcher", Instructions = "Watch.", DataSourceId = 1, CreatedByUserId = createdByUserId });
        actors
            .Setup(x => x.GetActorQueriesAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        actors
            .Setup(x => x.GetActorSubscriptionsAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        actors
            .Setup(x => x.GetExecutionHistoryAsync(9, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        actors
            .Setup(x => x.GetPendingPlansAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet([user], saved) },
                saved));

        return new GetAiActorDetailsHandler(actors.Object, factory.Object);
    }
}
