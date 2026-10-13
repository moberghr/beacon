using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Worker.Services;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.HostData;
using Beacon.Core.SavedQueries;
using Microsoft.EntityFrameworkCore;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// With the approval workflow on, a subscription run executes reviewed SQL only: its query's stored steps must be the
/// content of the active version, and in strict mode that version must carry an approved request for exactly its hash
/// (the rule saved-query MCP tools follow). A refused run executes nothing and is recorded as skipped with a neutral
/// reason; a run a request asks for is recorded the same way and answered 409.
/// </summary>
[TestFixture]
public class ReviewedRunTests
{
    private const int SubscriptionId = 21;

    private static readonly QueryContent Live = QueryContentHash.Of(null, [GovernanceStore.Snapshot("SELECT 1")]);

    [Test]
    public void WithoutAnActiveVersion_OnlyStrictModeRefuses()
    {
        ReviewedQueryContent.RunRefusal(Live, null, strict: false).Should().BeNull("a query never versioned runs as created");
        ReviewedQueryContent.RunRefusal(Live, null, strict: true).Should().Be(ReviewedQueryContent.NotReviewedMessage);
    }

    [Test]
    public void StoredSqlThatIsNotTheActiveVersion_IsRefused()
    {
        var active = Active(QueryContentHash.Of(null, [GovernanceStore.Snapshot("SELECT 0")]).Hash, approved: true);

        ReviewedQueryContent.RunRefusal(Live, active, strict: false).Should().Be(ReviewedQueryContent.NotReviewedMessage);
        ReviewedQueryContent.RunRefusal(Live, active, strict: true).Should().Be(ReviewedQueryContent.NotReviewedMessage);
    }

    [Test]
    public void TheActiveVersionsContent_RunsOutsideStrictMode_AndInStrictModeOnlyWhenApprovedForItsHash()
    {
        ReviewedQueryContent.RunRefusal(Live, Active(Live.Hash, approved: false), strict: false).Should().BeNull();
        ReviewedQueryContent.RunRefusal(Live, Active(Live.Hash, approved: false), strict: true).Should().Be(ReviewedQueryContent.NotReviewedMessage);
        ReviewedQueryContent.RunRefusal(Live, Active(Live.Hash, approved: true), strict: true).Should().BeNull();
        ReviewedQueryContent.RunRefusal(Live, Active(Live.Hash, approved: true, status: QueryVersionStatus.Archived), strict: true)
            .Should().Be(ReviewedQueryContent.NotReviewedMessage, "strict mode runs an Active version only");
    }

    // A version recorded before Beacon kept hashes is compared by the hash of its snapshot; strict mode refuses it until it
    // is approved again (an approval records the hash).
    [Test]
    public void AVersionWithoutAStoredHash_IsComparedByItsSnapshot_AndStrictModeRefusesIt()
    {
        var legacy = new ActiveVersionContent
        {
            VersionId = 1,
            Status = QueryVersionStatus.Active,
            StepsJson = """[{"stepOrder":1,"sqlValue":"SELECT 1","dataSourceId":3,"dataSourceName":"warehouse","name":"Step 1"}]""",
        };

        ReviewedQueryContent.RunRefusal(Live, legacy, strict: false).Should().BeNull();
        ReviewedQueryContent.RunRefusal(Live, legacy, strict: true).Should().Be(ReviewedQueryContent.NotReviewedMessage);
        ReviewedQueryContent.RunRefusal(Live, legacy with { StepsJson = "not json" }, strict: false)
            .Should().Be(ReviewedQueryContent.NotReviewedMessage, "a snapshot that cannot be read matches nothing");
    }

    [Test]
    public async Task ARun_OfStoredSqlThatIsNotTheActiveVersion_ExecutesNothing()
    {
        var store = Subscribed();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));
        var connections = Connections();

        var act = () => Service(store, WorkflowOn(), connections.Object).ExecuteQuery(SubscriptionId, CancellationToken.None);

        (await act.Should().ThrowAsync<QueryNotReviewedException>()).Which.DataSourceIds.Should().Equal([GovernanceStore.DataSourceId]);
        connections.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ARun_OfTheActiveVersionsContent_Executes()
    {
        var store = Subscribed();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));

        var connections = Connections();

        await ShouldReachTheSql(() => Service(store, WorkflowOn(), connections.Object).ExecuteQuery(SubscriptionId, CancellationToken.None), connections);
    }

    [Test]
    public async Task InStrictMode_ARunOfAnUnapprovedVersion_ExecutesNothing_AndOfAnApprovedOneExecutes()
    {
        var store = Subscribed();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var connections = Connections();
        var strict = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, RequireApprovedVersionToRun = true } };

        var unapproved = () => Service(store, strict, connections.Object).ExecuteQuery(SubscriptionId, CancellationToken.None);

        await unapproved.Should().ThrowAsync<QueryNotReviewedException>();
        connections.VerifyNoOtherCalls();

        store.AddRequest(active, "ext-ben", null, ApprovalStatus.Approved, approvedContentHash: active.ContentHash, reviewedBy: "ext-cid");
        await ShouldReachTheSql(() => Service(store, strict, connections.Object).ExecuteQuery(SubscriptionId, CancellationToken.None), connections);
    }

    // A query stored before the workflow was on and never versioned since runs as stored outside strict mode, and every
    // such run says so (by ids only).
    [Test]
    public async Task ARun_OfAQueryNeverVersioned_RunsOutsideStrictMode_AndIsLoggedEachTime()
    {
        var store = Subscribed();
        var connections = Connections();
        using var recorder = new LogRecorder();

        await ShouldReachTheSql(
            () => TestQueryServices.QueryService(store.Factory(), connections: connections.Object, configuration: WorkflowOn(), logger: recorder.For<QueryService>()).ExecuteQuery(SubscriptionId, CancellationToken.None),
            connections);

        var warning = recorder.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain($"Subscription {SubscriptionId} runs query {GovernanceStore.QueryId}").And.Contain("no version records").And.NotContain("SELECT");
    }

    [Test]
    public async Task ARun_OfAVersionedQuery_LogsNoSuchWarning()
    {
        var store = Subscribed();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var connections = Connections();
        using var recorder = new LogRecorder();

        await ShouldReachTheSql(
            () => TestQueryServices.QueryService(store.Factory(), connections: connections.Object, configuration: WorkflowOn(), logger: recorder.For<QueryService>()).ExecuteQuery(SubscriptionId, CancellationToken.None),
            connections);

        recorder.Contains("no version records").Should().BeFalse();
    }

    // An approval that lands between a run's read of the steps and its read of the active version replaces both: the run
    // reads them once more before refusing, and runs the approved content.
    [Test]
    public async Task ARun_WhoseQueryWasApprovedBetweenItsReads_ReadsOnceMoreAndRuns()
    {
        var before = Subscribed();
        before.Activate(before.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));
        var after = Subscribed();
        after.Activate(after.AddVersion(2, QueryVersionStatus.Active));
        var contexts = new Queue<IDbContextFactory<BeaconContext>>([before.Factory()]);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => (contexts.Count > 0 ? contexts.Dequeue() : after.Factory()).CreateDbContextAsync(ct));
        var connections = Connections();

        await ShouldReachTheSql(() => TestQueryServices.QueryService(factory.Object, connections: connections.Object, configuration: WorkflowOn()).ExecuteQuery(SubscriptionId, CancellationToken.None), connections);
    }

    // An active version whose snapshot cannot be read matches no stored SQL: the run is refused with a Warning of its own,
    // which names the version only.
    [Test]
    public async Task ARun_OfAnActiveVersionWhoseSnapshotCannotBeRead_IsRefusedWithItsOwnWarning()
    {
        var store = Subscribed();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active, legacy: true));
        active.StepsJson = "not json";
        var connections = Connections();
        using var recorder = new LogRecorder();

        var act = () => TestQueryServices.QueryService(store.Factory(), connections: connections.Object, configuration: WorkflowOn(), logger: recorder.For<QueryService>()).ExecuteQuery(SubscriptionId, CancellationToken.None);

        await act.Should().ThrowAsync<QueryNotReviewedException>();
        connections.VerifyNoOtherCalls();
        recorder.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain($"the snapshot of active version {active.Id} of query {GovernanceStore.QueryId} cannot be read").And.NotContain("SELECT");
    }

    // A refused run names the remedy in its Warning: save the query to submit its stored SQL, or restore and approve.
    [Test]
    public async Task ARefusedRun_NamesTheRemedy()
    {
        var store = Subscribed();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));
        using var recorder = new LogRecorder();

        var act = () => TestQueryServices.QueryService(store.Factory(), connections: Connections().Object, configuration: WorkflowOn(), logger: recorder.For<QueryService>()).ExecuteQuery(SubscriptionId, CancellationToken.None);

        await act.Should().ThrowAsync<QueryNotReviewedException>();
        recorder.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("Save the query to submit its stored SQL for approval, or restore a version and approve it").And.NotContain("SELECT");
    }

    // Strict mode counts only an Approved request that recorded exactly the active version's hash: one for another hash,
    // or a rejected or superseded one with the right hash, does not make the version runnable — for a run or an MCP tool.
    [TestCase(ApprovalStatus.Approved, false, false)]
    [TestCase(ApprovalStatus.Rejected, true, false)]
    [TestCase(ApprovalStatus.Superseded, true, false)]
    [TestCase(ApprovalStatus.Approved, true, true)]
    public async Task InStrictMode_OnlyAnApprovalOfExactlyTheActiveVersionsHash_Counts(ApprovalStatus status, bool rightHash, bool counts)
    {
        var store = Subscribed();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        store.AddRequest(active, "ext-ben", null, status, approvedContentHash: rightHash ? active.ContentHash : new string('0', 64), reviewedBy: "ext-cid");
        await using var context = await store.Factory().CreateDbContextAsync();

        var read = await ReviewedQueryContent.ActiveVersionAsync(context, GovernanceStore.QueryId, CancellationToken.None);
        var tool = SavedQueryRunnableVersion.ForQuery(context, GovernanceStore.QueryId, requireApprovedHash: true).ToList();

        read!.IsApprovedForItsHash.Should().Be(counts);
        ReviewedQueryContent.RunRefusal(Live, read, strict: true).Should().Be(counts ? null : ReviewedQueryContent.NotReviewedMessage);
        tool.Should().HaveCount(counts ? 1 : 0);
    }

    [Test]
    public async Task WithTheWorkflowOff_NothingIsCompared()
    {
        var store = Subscribed();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));

        var connections = Connections();

        await ShouldReachTheSql(() => Service(store, new BeaconConfiguration(), connections.Object).ExecuteQuery(SubscriptionId, CancellationToken.None), connections);
    }

    [Test]
    public async Task AScheduledRunOfUnreviewedSql_IsRecordedAsSkipped()
    {
        var store = Subscribed();

        await Jobs(store, request: null).ExecuteQuery(SubscriptionId, CancellationToken.None);

        ShouldBeSkipped(store);
    }

    [Test]
    public async Task ATestRunOfUnreviewedSql_IsRecordedAsSkipped_AndAnswered409()
    {
        var store = Subscribed();

        var act = () => Jobs(store, request: new DefaultHttpContext()).ExecuteQuery(SubscriptionId, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ReviewedQueryContent.NotReviewedMessage);
        ShouldBeSkipped(store);
    }

    // The run got as far as the step's SQL: the provider asked for the connection (which the test refuses, so the run
    // fails there).
    private static async Task ShouldReachTheSql(Func<Task> run, Mock<IDataSourceConnectionResolver> connections)
    {
        await run.Should().ThrowAsync<Exception>();
        connections.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.AtLeastOnce());
    }

    private static void ShouldBeSkipped(GovernanceStore store)
    {
        var run = store.Saved.OfType<QueryExecutionHistory>().Should().ContainSingle().Subject;
        run.NotificationStatus.Should().Be(NotificationStatus.Skipped);
        run.Comment.Should().Be(NotificationFailureReasons.NotApproved);
        run.ResultCount.Should().Be(0);
        run.DataSourceIds.Should().Equal([GovernanceStore.DataSourceId]);
        NotificationFailureReasons.Displayable(run.Comment).Should().Be(NotificationFailureReasons.NotApproved, "readers of the history see the reason");
    }

    private static ActiveVersionContent Active(string hash, bool approved, QueryVersionStatus status = QueryVersionStatus.Active) =>
        new()
        {
            VersionId = 1,
            Status = status,
            ContentHash = hash,
            IsApprovedForItsHash = approved,
        };

    private static GovernanceStore Subscribed()
    {
        var store = new GovernanceStore();
        store.Subscriptions.Add(new Subscription
        {
            Id = SubscriptionId,
            QueryId = GovernanceStore.QueryId,
            Query = store.Query,
            CronExpression = "0 * * * *",
            NotificationTrigger = NotificationTrigger.Always,
        });

        return store;
    }

    private static BeaconConfiguration WorkflowOn() => new() { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

    // Reached when a step's SQL is about to run: the provider asks for the data source's connection.
    private static Mock<IDataSourceConnectionResolver> Connections()
    {
        var connections = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        connections
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(new ExecutionReachedException());

        return connections;
    }

    private static QueryService Service(GovernanceStore store, BeaconConfiguration configuration, IDataSourceConnectionResolver connections) =>
        TestQueryServices.QueryService(store.Factory(), connections: connections, configuration: configuration);

    private static JobService Jobs(GovernanceStore store, HttpContext? request)
    {
        var queries = new Mock<IQueryService>();
        queries
            .Setup(x => x.ExecuteQuery(SubscriptionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueryNotReviewedException([GovernanceStore.DataSourceId]));
        var http = new HttpContextAccessor { HttpContext = request };

        return new JobService(
            store.Factory(),
            queries.Object,
            Mock.Of<INotificationService>(MockBehavior.Strict),
            Mock.Of<ITaskService>(MockBehavior.Strict),
            Mock.Of<IAnomalyDetectionService>(MockBehavior.Strict),
            Mock.Of<IDataQualityEvaluationService>(MockBehavior.Strict),
            NullLogger<JobService>.Instance,
            new RunAsScope(http),
            http);
    }

    private sealed class ExecutionReachedException : Exception;
}
