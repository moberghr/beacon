using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ControlTower;
using Beacon.Core.Handlers.DataQuality.GetDataContracts;
using Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Notifications;
using Beacon.Core.Handlers.QueryFolders;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Helpers;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Models.DataQuality;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// What the run, task, Control Tower, Home, data-quality, folder and learning lists keep, row by row, through their real
/// handlers over laid-out rows (in-memory sets, no database). The caller edits in the open project 1 and views the
/// restricted project 3, so a row of project 3 in an answer shows the list filters by the projects the caller may READ;
/// rows of the restricted project 2, of the archived project 4, of the closed unlinked source 50, of a query no single
/// project holds and of another user's query without steps never count; rows of the openly reachable source 60 and of
/// the caller's own query without steps do. Ids and totals are both asserted.
/// </summary>
[TestFixture]
public class ProjectAggregateRowLevelTests
{
    private const string Caller = "ext-ada";

    /// <summary>Editor in the open project 1, Viewer in the restricted project 3; sources 20, 30, 40 and 50 are not open.</summary>
    private static readonly ProjectAccessScope Scope = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor, [3] = ProjectRole.Viewer },
        includesOpen: true,
        notOpen: [20, 30, 40, 50],
        queryOwnerUserId: Caller);

    [Test]
    public async Task Notifications_AreTheRunsWhoseSourcesAProjectReadHolds_OrThatAreOpen_OrLegacyRunsOfAReadableQuery()
    {
        var page = await AnswerAsync<PagedList<NotificationEntry>>(new GetNotificationsQuery());

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([1, 3, 6, 8]);
        page.TotalCount.Should().Be(4);
        page.Items.Single(x => x.Id == 1).RecipientNames.Should().Equal("ops");
    }

    [Test]
    public async Task Tasks_AreThoseOfReadableSubscriptions()
    {
        var page = await AnswerAsync<PagedList<TaskEntry>>(new GetTasksQuery());

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([2100, 2101, 2300, 2600, 2900]);
        page.TotalCount.Should().Be(5);
        page.Items.Single(x => x.Id == 2100).ExecutionCount.Should().Be(2, "the subscription's runs since the task opened");
    }

    [Test]
    public async Task ControlTowerHealth_ListsTheReadableSubscriptions()
    {
        var page = await AnswerAsync<PagedList<ControlTowerSubscriptionHealthData>>(new GetControlTowerHealthQuery());

        page.Items.Select(x => x.SubscriptionId).Should().BeEquivalentTo([1100, 1300, 1600, 1900]);
        page.TotalCount.Should().Be(4);
        page.Items.Single(x => x.SubscriptionId == 1300).UnresolvedTaskCount.Should().Be(1);
        page.Items.Single(x => x.SubscriptionId == 1100).AnomalyCount30Days.Should().Be(1);
    }

    [Test]
    public async Task ControlTowerStatistics_CountTheReadableSubscriptions_TheirTasksAndAnomalies()
    {
        var answer = await AnswerAsync<GetControlTowerStatisticsResult>(new GetControlTowerStatisticsQuery());

        answer.Statistics.TotalSubscriptions.Should().Be(4);
        answer.Statistics.TotalUnresolvedTasks.Should().Be(4, "2100, 2300, 2600 and 2900; 2101 is resolved");
        answer.Statistics.TotalAnomalies30Days.Should().Be(3);
    }

    [Test]
    public async Task HomeTaskSummary_CountsTheReadableTasks()
    {
        var answer = await AnswerAsync<GetHomeTaskSummaryResult>(new GetHomeTaskSummaryQuery());

        answer.Should().Be(new GetHomeTaskSummaryResult(5, 4, 1));
    }

    [Test]
    public async Task HomeActivity_ListsOnlyReadableRunsSubscriptionsTasksAndAnomalies()
    {
        var answer = await AnswerAsync<GetHomeActivityResult>(new GetHomeActivityQuery(50));

        var titles = answer.Items.Select(x => x.Title).ToList();
        titles.Should().BeEquivalentTo(
        [
            "query 100 ran successfully",
            "query 100 ran successfully",
            "query 600 ran successfully",
            "query 300 failed",
            "subscription created for query 100",
            "subscription created for query 300",
            "subscription created for query 600",
            "subscription created for query 900",
            "task resolved for query 100",
            "new task opened for query 100",
            "new task opened for query 300",
            "new task opened for query 600",
            "new task opened for query 900",
            "anomaly detected on query 100",
            "anomaly detected on query 300",
            "anomaly detected on query 600"
        ]);
    }

    [Test]
    public async Task HomeTrends_CountOnlyWhatTheCallerReads()
    {
        var answer = await AnswerAsync<GetHomeTrendsResult>(new GetHomeTrendsQuery());

        answer.TotalSubscriptions.Should().Be(4);
        answer.QueryExecutions30d.Should().Be(4, "runs 1, 3, 6 and the legacy run 8");
        answer.NotificationsSent30d.Should().Be(3, "the notifications of runs 1, 3 and 6");
        answer.AnomaliesOpen.Should().Be(3);
        answer.DataSourcesOnline.Should().Be(4, "sources 10, 11, 30 and 60");
        answer.SlowestQueryName.Should().Be("query 300", "the slowest readable run; the hidden ones are slower still");
        answer.FastestQueryName.Should().Be("query 600");
    }

    [Test]
    public async Task ExecutionUptime_SeesOnlyReadableRuns()
    {
        var answer = await AnswerAsync<GetExecutionUptimeResult>(new GetExecutionUptimeQuery());

        answer.Ticks[^1].Should().Be("ok", "the failed runs this hour are hidden; the readable ones succeeded");
        answer.Ticks[^3].Should().Be("crit", "the readable run of project 3 failed two hours ago");
        answer.Ticks.Count(x => x != "muted").Should().Be(2);
    }

    [Test]
    public async Task DataQualityOverview_IsTheReadableSourcesScoresAndContracts()
    {
        var answer = await AnswerAsync<List<DataQualityOverviewData>>(new GetDataQualityOverviewQuery());

        answer.Select(x => x.DataSourceId).Should().BeEquivalentTo([10, 30, 60]);
        answer.Sum(x => x.TotalTables).Should().Be(3);
        answer.Single(x => x.DataSourceId == 10).ActiveContracts.Should().Be(1);
        answer.Single(x => x.DataSourceId == 30).ActiveContracts.Should().Be(1, "project 3 is read through the Viewer role");
        answer.Select(x => x.OwnerNotAuthorizedContracts).Should().Equal([1, 1, 0], "the skipped contracts of sources 10 and 30; source 20's is hidden");
    }

    [Test]
    public async Task DataQualityOverviewPage_IsTheReadableSourcesScoresAndContracts_PagedBySource()
    {
        var all = await AnswerAsync<PagedList<DataQualityOverviewData>>(new ListDataQualityOverviewQuery());
        var second = await AnswerAsync<PagedList<DataQualityOverviewData>>(new ListDataQualityOverviewQuery { Page = 1, PageSize = 2 });

        all.Items.Select(x => x.DataSourceId).Should().BeEquivalentTo([10, 30, 60]);
        all.TotalCount.Should().Be(3);
        all.Items.Sum(x => x.TotalTables).Should().Be(3);
        all.Items.Single(x => x.DataSourceId == 30).ActiveContracts.Should().Be(1, "project 3 is read through the Viewer role");
        all.Items.ToDictionary(x => x.DataSourceId, x => x.OwnerNotAuthorizedContracts).Should().Equal(new Dictionary<int, int> { [10] = 1, [30] = 1, [60] = 0 });
        second.TotalCount.Should().Be(3);
        second.PageCount.Should().Be(2);
        second.Items.Should().ContainSingle("page 2 of 2-row pages over three readable sources");
    }

    [Test]
    public async Task FoldersPage_CountsOnlyTheReadableQueries()
    {
        var page = await AnswerAsync<PagedList<QueryFolderData>>(new ListQueryFoldersQuery());

        page.TotalCount.Should().Be(page.Items.Count);
        page.Items.Select(x => (x.Id, x.QueryCount)).Should().BeEquivalentTo([(1, 1), (2, 1)]);
    }

    [Test]
    public async Task ControlTowerStatistics_CountTheReadableContractsWhoseScheduledEvaluationWasSkipped()
    {
        var answer = await AnswerAsync<GetControlTowerStatisticsResult>(new GetControlTowerStatisticsQuery());

        answer.Statistics.OwnerNotAuthorizedContracts.Should().Be(2, "the contracts of sources 10 and 30; source 20's is hidden");
    }

    [Test]
    public async Task DataContracts_SayWhichScheduledEvaluationsWereSkipped()
    {
        var page = await AnswerAsync<PagedList<DataContractData>>(new GetDataContractsQuery());

        page.Items.ToDictionary(x => x.DataSourceId, x => x.OwnerNotAuthorized).Should().Equal(new Dictionary<int, bool>
        {
            [10] = true,
            [30] = true,
            [60] = false
        });
    }

    [Test]
    public async Task Folders_CountOnlyTheReadableQueries()
    {
        var answer = await AnswerAsync<GetQueryFoldersResult>(new GetQueryFoldersQuery());

        answer.Folders.Select(x => (x.Id, x.QueryCount)).Should().BeEquivalentTo([(1, 1), (2, 1)]);
        answer.RootLevelQueryCount.Should().Be(2, "600 is openly reachable and 900 is the caller's own query without steps");
        answer.TotalQueryCount.Should().Be(4);
    }

    [Test]
    public async Task LearningStats_CountTheReadableProjectsSignalsPatternsAndPatches()
    {
        var answer = await AnswerAsync<LearningStatsResult>(new GetLearningStatsQuery());

        answer.TotalSignals.Should().Be(4, "three of project 1 and one of project 3; none without a project");
        answer.SuccessRate.Should().Be(0.5);
        answer.PatternsApproved.Should().Be(1);
        answer.PatternsPending.Should().Be(1);
        answer.PatternsRejected.Should().Be(0, "the rejected pattern is project 2's");
        answer.PatchesApplied.Should().Be(1);
        answer.PatchesProposed.Should().Be(1);
        answer.ProblemTables.Should().ContainSingle().Which.Should().BeEquivalentTo(new { TablesUsed = "orders", TotalQueries = 3, ErrorCount = 1 }, x => x.ExcludingMissingMembers());
    }

    private static async Task<T> AnswerAsync<T>(object request)
    {
        var answer = await new ListHandlerHarness(Scope).AnswerAsync(request, new World().Factory());

        return (T)answer!;
    }

    /// <summary>
    /// Projects 1 (open), 2 (restricted), 3 (restricted, the caller a Viewer) and 4 (archived) link sources 10 and 11, 11
    /// and 20, 30, and 40; 50 is unlinked and closed, 60 unlinked and open. Queries read 10 (100), 20 (200), 30 (300), 10
    /// and 30 (310), 40 (400) and 60 (600); 900 has no steps and is the caller's, 901 has none and is another's. Each
    /// query has a subscription (1000 + its id), runs, tasks, notifications, anomalies, scores, contracts and signals
    /// laid out so that only the readable ones are counted.
    /// </summary>
    private sealed class World
    {
        private readonly List<object> _saved = [];
        private readonly List<Project> _projects;
        private readonly Dictionary<int, DataSource> _sources;
        private readonly List<ProjectDataSource> _links = [];
        private readonly List<Query> _queries = [];
        private readonly List<QueryStep> _steps = [];
        private readonly List<QueryFolder> _folders = [];
        private readonly List<Subscription> _subscriptions = [];
        private readonly List<QueryExecutionHistory> _runs = [];
        private readonly List<Notification> _notifications = [];
        private readonly List<QueryTask> _tasks = [];
        private readonly List<AnomalyEvent> _anomalies = [];
        private readonly List<DataQualityScore> _scores = [];
        private readonly List<DataContract> _contracts = [];
        private readonly List<McpQuerySignal> _signals = [];
        private readonly List<McpLearnedPattern> _patterns = [];
        private readonly List<McpDocumentationPatch> _patches = [];
        private readonly Recipient _recipient = new() { Id = 1, Name = "ops", Destination = "ops@example.test", NotificationType = NotificationType.Email };
        private readonly DateTime _now = DateTime.UtcNow;

        public World()
        {
            _projects =
            [
                new Project { Id = 1, Name = "Open" },
                new Project { Id = 2, Name = "Restricted", RequiresMembership = true },
                new Project { Id = 3, Name = "Member", RequiresMembership = true },
                new Project { Id = 4, Name = "Archived", ArchivedTime = DateTime.UtcNow }
            ];
            _sources = new[] { 10, 11, 20, 30, 40, 50, 60 }
                .Select(x => new DataSource { Id = x, Name = $"source {x}", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc", OpenWhenUnlinked = x == 60 })
                .ToDictionary(x => x.Id);
            Link(1, 10);
            Link(1, 11);
            Link(2, 11);
            Link(2, 20);
            Link(3, 30);
            Link(4, 40);

            _folders.Add(new QueryFolder { Id = 1, Name = "Finance", Path = "/Finance", SortOrder = 1 });
            _folders.Add(new QueryFolder { Id = 2, Name = "Audit", Path = "/Audit", SortOrder = 2 });
            AddQuery(100, null, 1, 10);
            AddQuery(200, null, 1, 20);
            AddQuery(300, null, 2, 30);
            AddQuery(310, null, 1, 10, 30);
            AddQuery(400, null, 2, 40);
            AddQuery(600, null, null, 60);
            AddQuery(900, Caller, null);
            AddQuery(901, "ext-other", 2);

            foreach (var query in _queries)
            {
                AddSubscription(1000 + query.Id, query);
            }

            // Recorded runs read their subscription's sources; 8 and 9 were recorded before runs kept them.
            AddRun(1, 1100, [10], NotificationStatus.NotificationSent, _now, 100);
            AddRun(2, 1200, [20], NotificationStatus.Failed, _now, 9000);
            AddRun(3, 1300, [30], NotificationStatus.Failed, _now.AddHours(-2), 500);
            AddRun(4, 1310, [10, 30], NotificationStatus.Failed, _now, 9000);
            AddRun(5, 1400, [40], NotificationStatus.Failed, _now, 9000);
            AddRun(6, 1600, [60], NotificationStatus.NotificationSent, _now, 10);
            AddRun(7, 1901, [50], NotificationStatus.Failed, _now, 9000);
            AddRun(8, 1100, null, NotificationStatus.NotificationSent, _now, 200);
            AddRun(9, 1200, null, NotificationStatus.Failed, _now, 9000);
            foreach (var runId in new[] { 1, 2, 3, 4, 6 })
            {
                AddNotification(runId);
            }

            foreach (var subscription in _subscriptions)
            {
                AddTask(subscription.Id + 1000, subscription, resolved: false);
            }

            AddTask(2101, _subscriptions.Single(x => x.Id == 1100), resolved: true);
            foreach (var subscriptionId in new[] { 1100, 1200, 1300, 1600, 1901 })
            {
                AddAnomaly(subscriptionId);
            }

            foreach (var sourceId in new[] { 10, 20, 30, 40, 50, 60 })
            {
                _scores.Add(new DataQualityScore { Id = sourceId, DataSourceId = sourceId, DataSource = _sources[sourceId], SchemaName = "s", TableName = "t", Score = 90, EvaluatedAt = _now });
                _contracts.Add(new DataContract { Id = 600 + sourceId, Name = $"contract {sourceId}", SchemaName = "s", TableName = "t", CronExpression = "0 * * * *", IsEnabled = true, DataSourceId = sourceId, DataSource = _sources[sourceId] });
            }

            // The scheduled evaluations of the contracts on 10, 20 and 30 were skipped: their owners may no longer run them.
            foreach (var contract in _contracts.Where(x => x.DataSourceId is 10 or 20 or 30))
            {
                contract.OwnerNotAuthorizedSince = _now.AddDays(-1);
            }

            AddSignal(1, "orders", true);
            AddSignal(1, "orders", true);
            AddSignal(1, "orders", false);
            AddSignal(3, "ledger", false);
            AddSignal(2, "orders", false);
            AddSignal(2, "orders", false);
            AddSignal(4, "orders", false);
            AddSignal(null, "orders", true);
            _patterns.Add(new McpLearnedPattern { Id = 801, ProjectId = 1, DataSourceId = 10, SchemaName = "s", TableName = "t", PatternContent = "p", Status = McpPatternStatus.Approved });
            _patterns.Add(new McpLearnedPattern { Id = 802, ProjectId = 2, DataSourceId = 20, SchemaName = "s", TableName = "t", PatternContent = "p", Status = McpPatternStatus.Rejected });
            _patterns.Add(new McpLearnedPattern { Id = 803, ProjectId = 3, DataSourceId = 30, SchemaName = "s", TableName = "t", PatternContent = "p", Status = McpPatternStatus.Pending });
            _patches.Add(new McpDocumentationPatch { Id = 901, ProjectId = 1, DataSourceId = 10, TargetIdentifier = "t", ProposedContent = "p", Reasoning = "r", Status = McpDocPatchStatus.Applied });
            _patches.Add(new McpDocumentationPatch { Id = 902, ProjectId = 2, DataSourceId = 20, TargetIdentifier = "t", ProposedContent = "p", Reasoning = "r", Status = McpDocPatchStatus.Applied });
            _patches.Add(new McpDocumentationPatch { Id = 903, ProjectId = 3, DataSourceId = 30, TargetIdentifier = "t", ProposedContent = "p", Reasoning = "r", Status = McpDocPatchStatus.Proposed });
        }

        public IDbContextFactory<BeaconContext> Factory()
        {
            var users = new List<BeaconUser> { new() { Id = 3, ExternalId = Caller, UserName = "ada", IsEnabled = true } };
            var sets = new Dictionary<Type, object>
            {
                [typeof(Project)] = RecordingBeaconContext.MemorySet(_projects, _saved),
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(_sources.Values.ToList(), _saved),
                [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(_links, _saved),
                [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(new List<ProjectMember>(), _saved),
                [typeof(Query)] = RecordingBeaconContext.MemorySet(_queries, _saved),
                [typeof(QueryStep)] = RecordingBeaconContext.MemorySet(_steps, _saved),
                [typeof(QueryFolder)] = RecordingBeaconContext.MemorySet(_folders, _saved),
                [typeof(Subscription)] = RecordingBeaconContext.MemorySet(_subscriptions, _saved),
                [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(_runs, _saved),
                [typeof(Notification)] = RecordingBeaconContext.MemorySet(_notifications, _saved),
                [typeof(Recipient)] = RecordingBeaconContext.MemorySet(new List<Recipient> { _recipient }, _saved),
                [typeof(QueryTask)] = RecordingBeaconContext.MemorySet(_tasks, _saved),
                [typeof(AnomalyEvent)] = RecordingBeaconContext.MemorySet(_anomalies, _saved),
                [typeof(AnomalyConfig)] = RecordingBeaconContext.MemorySet(new List<AnomalyConfig>(), _saved),
                [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(_scores, _saved),
                [typeof(DataContract)] = RecordingBeaconContext.MemorySet(_contracts, _saved),
                [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(_signals, _saved),
                [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(_patterns, _saved),
                [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(_patches, _saved),
                [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, _saved)
            };

            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved, new SqlCapture()));

            return factory.Object;
        }

        private void Link(int projectId, int dataSourceId)
        {
            var link = new ProjectDataSource { ProjectId = projectId, DataSourceId = dataSourceId, Project = _projects.Single(x => x.Id == projectId), DataSource = _sources[dataSourceId] };
            _links.Add(link);
            link.Project.DataSources.Add(link);
        }

        private void AddQuery(int id, string? ownerUserId, int? folderId, params int[] dataSourceIds)
        {
            var folder = _folders.SingleOrDefault(x => x.Id == folderId);
            var query = new Query { Id = id, Name = $"query {id}", OwnerUserId = ownerUserId, FolderId = folderId, Folder = folder, CreatedTime = _now.AddDays(-1) };
            folder?.Queries.Add(query);
            var order = 1;
            foreach (var dataSourceId in dataSourceIds)
            {
                var step = new QueryStep
                {
                    Id = (id * 10) + order,
                    QueryId = id,
                    Query = query,
                    DataSourceId = dataSourceId,
                    DataSource = _sources[dataSourceId],
                    StepOrder = order++,
                    SqlValue = "select 1"
                };
                query.Steps.Add(step);
                _steps.Add(step);
            }

            _queries.Add(query);
        }

        private void AddSubscription(int id, Query query)
        {
            var subscription = new Subscription { Id = id, QueryId = query.Id, Query = query, CronExpression = "0 * * * *", CreatedTime = _now.AddDays(-1) };
            query.Subscriptions.Add(subscription);
            _subscriptions.Add(subscription);
        }

        private void AddRun(int id, int subscriptionId, int[]? dataSourceIds, NotificationStatus status, DateTime createdTime, double executionTimeMs)
        {
            var subscription = _subscriptions.Single(x => x.Id == subscriptionId);
            var run = new QueryExecutionHistory
            {
                Id = id,
                SubscriptionId = subscriptionId,
                Subscription = subscription,
                ResultCount = id,
                CompiledSql = "select 1",
                NotificationStatus = status,
                ExecutionTimeMs = executionTimeMs,
                DataSourceIds = dataSourceIds,
                CreatedTime = createdTime
            };
            subscription.QueryExecutionHistory!.Add(run);
            _runs.Add(run);
        }

        private void AddNotification(int runId)
        {
            var run = _runs.Single(x => x.Id == runId);
            var notification = new Notification
            {
                Id = runId,
                QueryExecutionHistoryId = runId,
                QueryExecutionHistory = run,
                RecipientId = _recipient.Id,
                Recipient = _recipient,
                Type = NotificationType.Email,
                SentAt = run.CreatedTime
            };
            run.Notifications.Add(notification);
            _notifications.Add(notification);
        }

        private void AddTask(int id, Subscription subscription, bool resolved)
        {
            _tasks.Add(new QueryTask
            {
                Id = id,
                SubscriptionId = subscription.Id,
                Subscription = subscription,
                LatestResultCount = 1,
                Resolved = resolved,
                ResolvedAt = resolved ? _now.AddMinutes(-5) : null,
                CreatedTime = _now.AddHours(-3)
            });
        }

        private void AddAnomaly(int subscriptionId)
        {
            _anomalies.Add(new AnomalyEvent
            {
                Id = subscriptionId,
                SubscriptionId = subscriptionId,
                Subscription = _subscriptions.Single(x => x.Id == subscriptionId),
                DetectedTime = _now.AddMinutes(-30),
                CurrentValue = 1
            });
        }

        private void AddSignal(int? projectId, string tables, bool successful)
        {
            _signals.Add(new McpQuerySignal
            {
                Id = _signals.Count + 1,
                ProjectId = projectId,
                Tool = "ask",
                Question = "q",
                TablesUsed = tables,
                IsSuccessful = successful,
                CreatedTime = _now.AddDays(-1)
            });
        }
    }
}
