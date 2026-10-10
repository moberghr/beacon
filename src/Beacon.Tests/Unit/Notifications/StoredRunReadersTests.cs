using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ControlTower;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The Home activity feed, the Control Tower detail, a task's executions and result history, and a subscription's
/// anomaly chart list stored runs by the rule the notifications endpoints use: a project-restricted caller sees only
/// the runs its projects may read, and a run's stored comment shows only as a recorded failure reason.
/// </summary>
[TestFixture]
public class StoredRunReadersTests
{
    private const int SubscriptionId = 50;
    private const int TaskId = 60;
    private const int ProjectOneSource = 10;
    private const int ProjectTwoSource = 20;
    private const string LegacyComment = "connection to 10.0.0.5 refused (raw driver text)";
    private const string RecordedComment = "Recipient 3: " + NotificationFailureReasons.TimedOut;

    private static readonly DateTime Yesterday = DateTime.UtcNow.AddDays(-1);

    [Test]
    public async Task HomeActivity_ShowsARunsFailureOnlyAsARecordedReason()
    {
        var result = await HomeActivity(Interactive());

        var failures = result.Items
            .Where(x => x.Tone == "crit")
            .ToDictionary(x => x.Timestamp, x => x.Meta);
        failures[At(1)].Should().Be(NotificationFailureReasons.Generic);
        failures[At(2)].Should().Be(RecordedComment);
        failures[At(3)].Should().Be("execution timed out");
        result.Items.Should().NotContain(x => x.Meta != null && x.Meta.Contains("10.0.0.5"));
    }

    [Test]
    public async Task HomeActivity_ProjectRestrictedCaller_SeesOnlyItsProjectsRuns()
    {
        var result = await HomeActivity(ApiKey("[1]"));

        var runs = result.Items
            .Where(x => x.Title.StartsWith("Report", StringComparison.Ordinal))
            .Select(x => x.Timestamp)
            .ToList();
        runs.Should().BeEquivalentTo([At(1), At(2), At(4)], "runs 3 (project two) and 5 (no recorded sources) are not its projects'");
    }

    [Test]
    public async Task HomeActivity_ScopedCallerWithoutProjects_SeesNoRun()
    {
        var result = await HomeActivity(Principal(("auth_method", "api_key"), ("scope", "Read")));

        result.Items.Should().NotContain(x => x.Title.StartsWith("Report", StringComparison.Ordinal));
    }

    [Test]
    public async Task ControlTowerDetail_ShowsOnlyRecordedReasons()
    {
        var detail = await ControlTower().GetSubscriptionDetail(SubscriptionId, 30, StoredRunScope.Unrestricted, CancellationToken.None);

        detail!.RecentExecutions.Select(x => x.ErrorMessage).Should().BeEquivalentTo(
            [NotificationFailureReasons.Generic, RecordedComment, null, null, null]);
    }

    [Test]
    public async Task ControlTowerDetail_ProjectRestrictedCaller_SeesOnlyItsProjectsRuns()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = ApiKey("[2]") } };
        var handler = new GetControlTowerSubscriptionDetailHandler(ControlTower(), accessor);

        var result = await handler.Handle(new GetControlTowerSubscriptionDetailQuery(SubscriptionId), CancellationToken.None);

        result.Detail.RecentExecutions.Select(x => x.ExecutionId).Should().Equal(3);
    }

    [Test]
    public async Task TaskExecutionsAndResultHistory_ProjectRestrictedCaller_SeeOnlyItsProjectsRuns()
    {
        var caller = ApiKey("[1]");

        var executions = await TaskExecutions(caller);
        var history = await TaskResultHistory(caller);

        executions.Executions.Select(x => x.Id).Should().Equal([4, 2, 1], "runs 3 (project two) and 5 (no recorded sources) are not its projects'");
        history.Points.Select(x => x.SampledAt).Should().Equal(At(1), At(2), At(4));
    }

    [Test]
    public async Task TaskExecutionsAndResultHistory_InteractiveCaller_SeeEveryRun()
    {
        var executions = await TaskExecutions(Interactive());
        var history = await TaskResultHistory(Interactive());

        executions.Executions.Select(x => x.Id).Should().Equal(5, 4, 3, 2, 1);
        history.Points.Select(x => x.SampledAt).Should().Equal(At(1), At(2), At(3), At(4), At(5));
    }

    [Test]
    public async Task TaskExecutionsAndResultHistory_ScopedCallerWithoutProjects_SeeNoRun()
    {
        var caller = Principal(("auth_method", "api_key"), ("scope", "Read"));

        var executions = await TaskExecutions(caller);
        var history = await TaskResultHistory(caller);

        executions.Executions.Should().BeEmpty();
        history.Points.Should().BeEmpty();
    }

    [Test]
    public async Task AnomalyChart_ProjectRestrictedCaller_SeesOnlyItsProjectsRuns()
    {
        var restricted = await AnomalyChart(ApiKey("[2]"));
        var interactive = await AnomalyChart(Interactive());

        restricted.Points.Select(x => x.QueryExecutionHistoryId).Should().Equal(3);
        interactive.Points.Select(x => x.QueryExecutionHistoryId).Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public async Task TaskExecutions_RestrictedRead_Translates()
    {
        var capture = new SqlCapture().ThenScalar(SubscriptionId).ThenNoRows();

        await TaskService(capture.Factory()).GetTaskExecutionHistory(TaskId, StoredRunScope.WithinProjects([1]), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the task's subscription, then its runs");
        RestrictedPredicateOf(capture.Commands[1]);
        capture.CommandParameters[1].Values.OfType<IEnumerable<int>>().Should().ContainSingle().Which.Should().Equal(1);
        capture.CommandParameters[1].Values.Should().Contain(SubscriptionId);
    }

    [Test]
    public async Task TaskResultHistory_RestrictedRead_Translates()
    {
        var capture = new SqlCapture().ThenScalar(SubscriptionId).ThenNoRows();

        await TaskService(capture.Factory()).GetResultCountHistory(TaskId, StoredRunScope.WithinProjects([2]), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the task's subscription, then its runs");
        RestrictedPredicateOf(capture.Commands[1]);
        capture.CommandParameters[1].Values.OfType<IEnumerable<int>>().Should().ContainSingle().Which.Should().Equal(2);
    }

    [Test]
    public async Task TaskExecutions_Unrestricted_ReadsWithoutThePredicate_Translates()
    {
        var capture = new SqlCapture().ThenScalar(SubscriptionId).ThenNoRows();

        await TaskService(capture.Factory()).GetTaskExecutionHistory(TaskId, StoredRunScope.Unrestricted, CancellationToken.None);

        capture.Commands[1].Should().NotContain("project_data_sources");
    }

    private static async Task<GetHomeActivityResult> HomeActivity(ClaimsPrincipal caller)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };

        return await new GetHomeActivityHandler(Factory(), accessor).Handle(new GetHomeActivityQuery(50), CancellationToken.None);
    }

    private static ControlTowerService ControlTower()
    {
        return new ControlTowerService(Factory(), new MemoryCache(new MemoryCacheOptions()));
    }

    private static async Task<TaskExecutionsResult> TaskExecutions(ClaimsPrincipal caller)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };

        return await new GetTaskExecutionsHandler(TaskService(Factory()), accessor).Handle(new GetTaskExecutionsQuery(TaskId), CancellationToken.None);
    }

    private static async Task<TaskResultHistoryResult> TaskResultHistory(ClaimsPrincipal caller)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };

        return await new GetTaskResultHistoryHandler(TaskService(Factory()), accessor).Handle(new GetTaskResultHistoryQuery(TaskId), CancellationToken.None);
    }

    private static async Task<GetSubscriptionAnomalyChartResult> AnomalyChart(ClaimsPrincipal caller)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };
        var service = new AnomalyDetectionService(Factory(), NullLogger<AnomalyDetectionService>.Instance);

        return await new GetSubscriptionAnomalyChartHandler(service, accessor).Handle(new GetSubscriptionAnomalyChartQuery(SubscriptionId), CancellationToken.None);
    }

    private static TaskService TaskService(IDbContextFactory<BeaconContext> factory)
    {
        return new TaskService(factory, NullLogger<TaskService>.Instance);
    }

    // The run must have recorded data sources, at least one, and some allowed project must hold each of them.
    private static void RestrictedPredicateOf(string sql)
    {
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        where.Should().Contain("q.data_source_ids IS NOT NULL");
        where.Should().Contain("cardinality(q.data_source_ids) > 0");
        where.Should().Contain("unnest(q.data_source_ids)");
        where.Should().Contain("FROM beacon.project_data_sources");
    }

    private static DateTime At(int run) => Yesterday.AddMinutes(run);

    private static IDbContextFactory<BeaconContext> Factory()
    {
        var query = new Query { Id = 100, Name = "Report" };
        var subscription = new Subscription { Id = SubscriptionId, QueryId = query.Id, Query = query, CronExpression = "0 * * * *" };
        var runs = new List<QueryExecutionHistory>
        {
            Run(1, subscription, NotificationStatus.Failed, LegacyComment, ProjectOneSource),
            Run(2, subscription, NotificationStatus.Failed, RecordedComment, ProjectOneSource),
            Run(3, subscription, NotificationStatus.Timeout, null, ProjectTwoSource),
            Run(4, subscription, NotificationStatus.NotificationSent, null, ProjectOneSource),
            Run(5, subscription, NotificationStatus.NotificationSent, null, null)
        };
        // Opened long ago, so it is outside the Home activity window.
        var task = new QueryTask
        {
            Id = TaskId,
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            LatestResultCount = 1,
            CreatedTime = Yesterday.AddYears(-1)
        };
        var memberships = new List<ProjectDataSource>
        {
            new() { ProjectId = 1, DataSourceId = ProjectOneSource },
            new() { ProjectId = 2, DataSourceId = ProjectTwoSource }
        };
        var sets = new Dictionary<Type, object>
        {
            [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(runs, []),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(memberships, []),
            [typeof(Subscription)] = RecordingBeaconContext.MemorySet(new List<Subscription> { subscription }, []),
            [typeof(QueryTask)] = RecordingBeaconContext.MemorySet(new List<QueryTask> { task }, []),
            [typeof(AnomalyEvent)] = RecordingBeaconContext.MemorySet(new List<AnomalyEvent>(), []),
            [typeof(AnomalyConfig)] = RecordingBeaconContext.MemorySet(new List<AnomalyConfig> { new() { SubscriptionId = SubscriptionId, Enabled = true } }, []),
            [typeof(AnomalyBaseline)] = RecordingBeaconContext.MemorySet(new List<AnomalyBaseline>(), [])
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, []));

        return factory.Object;
    }

    private static QueryExecutionHistory Run(
        int id,
        Subscription subscription,
        NotificationStatus status,
        string? comment,
        params int[]? dataSourceIds)
    {
        return new QueryExecutionHistory
        {
            Id = id,
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            ResultCount = 1,
            CompiledSql = "SELECT 1",
            NotificationStatus = status,
            ExecutionTimeMs = 5,
            Comment = comment,
            DataSourceIds = dataSourceIds,
            CreatedTime = At(id)
        };
    }

    private static ClaimsPrincipal Interactive()
    {
        return Principal((ClaimTypes.NameIdentifier, "ext-1"));
    }

    private static ClaimsPrincipal ApiKey(string allowedProjects)
    {
        return Principal(("auth_method", "api_key"), ("scope", "Read"), ("allowed_projects", allowedProjects));
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims.Select(x => new Claim(x.Type, x.Value)), "Test"));
    }
}
