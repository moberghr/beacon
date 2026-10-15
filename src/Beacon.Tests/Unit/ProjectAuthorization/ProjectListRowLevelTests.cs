using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.DataQuality.GetDataContracts;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.QueryVersions;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// What each list keeps, row by row, through its real handler over laid-out rows (in-memory sets, no database): a
/// caller who edits in the open project 1 and views the restricted project 3 sees rows whose project it reads, whose data
/// sources one project it reads holds all of (or that are all openly reachable), and the queries without steps it owns;
/// never the rows of the restricted project 2, of the archived project 4, of a source no project links and that is
/// closed, or of a query whose sources no single project holds. Every filtered list also sends the caller's Read
/// projects, not its Write projects, as the scope it filters by.
/// </summary>
[TestFixture]
public class ProjectListRowLevelTests
{
    private const string Caller = "ext-ada";

    /// <summary>Editor in the open project 1, Viewer in the restricted project 3; sources 20, 30, 40 and 50 are not open.</summary>
    private static readonly ProjectAccessScope Scope = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor, [3] = ProjectRole.Viewer },
        includesOpen: true,
        notOpen: [20, 30, 40, 50],
        queryOwnerUserId: Caller);

    public static IEnumerable<TestCaseData> FilteredRequests() =>
        ProjectListScopeShapeTests.FilteredRequests()
            .Select(x => new TestCaseData(x.Arguments[0]).SetName($"FilteredRequest_FiltersByTheReadProjects({x.Arguments[0]!.GetType().Name})"));

    [TestCaseSource(nameof(FilteredRequests))]
    public async Task FilteredRequest_FiltersByTheReadProjects_NotTheWriteProjects(object request)
    {
        var capture = await new ListHandlerHarness(Scope).CaptureAsync(request);

        var idLists = capture.CommandParameters
            .SelectMany(x => x.Values)
            .OfType<IEnumerable<int>>()
            .Select(x => x.ToList())
            .ToList();

        idLists.Should().Contain(x => x.SequenceEqual(new[] { 1, 3 }), "the list is filtered by the projects the caller may read");
        idLists.Should().NotContain(x => x.SequenceEqual(new[] { 1 }), "the projects the caller may write are not the ones it reads");
    }

    [Test]
    public async Task Projects_AreTheOnesTheCallerReads()
    {
        var page = await AnswerAsync<PagedList<ProjectSummaryEntry>>(new GetProjectsQuery());

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([1, 3]);
        page.TotalCount.Should().Be(2);
        page.Items.Single(x => x.Id == 3).MyRole.Should().Be(ProjectRole.Viewer);
    }

    [Test]
    public async Task DataSources_AreTheReadableOnes_WithReadableProjectsNamed_AndReadableQueriesCounted()
    {
        var page = await AnswerAsync<PagedList<DataSourceEntry>>(new GetDataSourcesQuery());

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([10, 11, 30, 60], "linked to a project read, or openly reachable");
        page.TotalCount.Should().Be(4);
        var shared = page.Items.Single(x => x.Id == 11);
        shared.Projects.Select(x => x.Id).Should().Equal(1);
        shared.OtherProjectCount.Should().Be(1, "the restricted project 2 also links it, and is only counted");
        page.Items.Single(x => x.Id == 10).QueryCount.Should().Be(2, "the query also reading source 30 is held by no single project");
        page.Items.Single(x => x.Id == 30).QueryCount.Should().Be(1);
        page.Items.Should().OnlyContain(x => x.MigrationJobsCount == 0, "migration jobs are counted for Admins only");
    }

    [Test]
    public async Task DataSources_CarryTheTransportBadge_UnknownWhenTheConnectionCannotBeRead()
    {
        var world = new World();
        world.Source(10).EncryptedConnectionData = new EncryptionService(TestEncryptionKey.Value).Encrypt("Host=db;SSL Mode=VerifyFull");

        var page = await AnswerAsync<PagedList<DataSourceEntry>>(new GetDataSourcesQuery(), world);

        page.Items.Single(x => x.Id == 10).TransportVerified.Should().BeFalse("no connector is registered for an engine-less source, so nothing verifies it");
        page.Items.Single(x => x.Id == 11).TransportVerified.Should().BeNull("its stored value cannot be decrypted");
    }

    [Test]
    public async Task DataSources_CountMigrationJobs_ForAnAdminOnly()
    {
        var listForAdmin = string.Join("\n", await new ListHandlerHarness(TestProjectAccess.Unrestricted).SendAsync(new GetDataSourcesQuery()));
        var listForMember = string.Join("\n", await new ListHandlerHarness(Scope).SendAsync(new GetDataSourcesQuery()));
        var detailForAdmin = string.Join("\n", await new ListHandlerHarness(TestProjectAccess.Unrestricted).SendAsync(new GetDataSourceQuery(10)));
        var detailForMember = string.Join("\n", await new ListHandlerHarness(Scope).SendAsync(new GetDataSourceQuery(10)));

        listForAdmin.Should().Contain("migration_jobs");
        detailForAdmin.Should().Contain("migration_jobs");
        listForMember.Should().NotContain("migration_jobs");
        detailForMember.Should().NotContain("migration_jobs");
    }

    [Test]
    public async Task Queries_AreTheOnesOneReadableProjectHolds_OrOpen_OrStepLessAndOwned()
    {
        var page = await AnswerAsync<PagedList<QueryData>>(new GetQueriesQuery());

        page.Items.Select(x => x.QueryId).Should().BeEquivalentTo([100, 300, 600, 700, 900]);
        page.TotalCount.Should().Be(5);
    }

    [Test]
    public async Task Subscriptions_AreThoseOfReadableQueries()
    {
        var page = await AnswerAsync<PagedList<SubscriptionEntry>>(new GetSubscriptionsQuery());

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([1100, 1300, 1900]);
        page.TotalCount.Should().Be(3);
    }

    [Test]
    public async Task ArchivedSubscriptions_AreReadByTheirQuery_OrByTheirLastRecordedRun()
    {
        var world = new World();
        world.ArchivedSubscriptions();

        var page = await AnswerAsync<PagedList<SubscriptionEntry>>(new GetSubscriptionsQuery { Archived = true }, world);

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([2100, 2901], "its query's live steps are readable, or its last run read only readable sources");
        page.TotalCount.Should().Be(2);
    }

    [Test]
    public async Task ChangeHistory_IsReadByTheSourceEachChangeWasMadeAgainst()
    {
        var page = await AnswerAsync<PagedList<QueryChangeHistoryItem>>(new GetQueryChangeHistoryQuery { QueryId = 700 });

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([7002, 7003], "a change made against the restricted source stays hidden after the step moved");
        page.TotalCount.Should().Be(2);
    }

    [Test]
    public async Task Versions_AreReadByTheSourcesEachOneRecorded_BeforePaging()
    {
        var page = await AnswerAsync<PagedList<QueryVersionSummary>>(new GetQueryVersionsQuery { QueryId = 700 });
        var firstPage = await AnswerAsync<PagedList<QueryVersionSummary>>(new GetQueryVersionsQuery { QueryId = 700, PageSize = 2 });

        page.Items.Select(x => x.Id).Should().BeEquivalentTo(
            [7102, 7103, 7105, 7109],
            "held by a project read (10 by 1, 30 by 3), or all openly reachable, recorded or named by a legacy snapshot");
        page.TotalCount.Should().Be(4);
        firstPage.Items.Should().HaveCount(2);
        firstPage.TotalCount.Should().Be(4, "the count leaves out the same versions as the page");
    }

    [Test]
    public async Task VersionsWithoutDataSources_AreTheQueryOwners()
    {
        var owned = await AnswerAsync<PagedList<QueryVersionSummary>>(new GetQueryVersionsQuery { QueryId = 900 });
        var others = await AnswerAsync<PagedList<QueryVersionSummary>>(new GetQueryVersionsQuery { QueryId = 901 });

        owned.Items.Select(x => x.Id).Should().BeEquivalentTo([9001, 9002], "recorded without sources, or a legacy snapshot without steps");
        owned.TotalCount.Should().Be(2);
        others.Items.Should().BeEmpty();
        others.TotalCount.Should().Be(0);
    }

    [Test]
    public async Task Versions_AreAllListed_ForAnUnrestrictedCaller()
    {
        var answer = await new ListHandlerHarness(TestProjectAccess.Unrestricted).AnswerAsync(new GetQueryVersionsQuery { QueryId = 700 }, new World().Factory());
        var page = (PagedList<QueryVersionSummary>)answer!;

        page.Items.Should().HaveCount(9);
        page.TotalCount.Should().Be(9);
    }

    [TestCase(7101, false)]
    [TestCase(7102, true)]
    [TestCase(7104, false)]
    [TestCase(7105, true)]
    public async Task TheQuerysApprovedVersion_IsNamed_OnlyToACallerWhoReadsIt(int versionId, bool named)
    {
        var world = new World();
        world.MakeRunnable(700, versionId);
        var handler = new GetQueryDetailHandler(DetailsWithRunnableVersion(7), world.Factory(), TestProjectAccess.Evaluator(Scope));

        var details = await handler.Handle(new GetQueryDetailQuery { QueryId = 700 }, CancellationToken.None);

        details.McpToolRunnableVersionNumber.Should().Be(named ? 7 : null);
        details.McpToolIssue.Should().Be(named ? null : GetQueryDetailHandler.UnreadableRunnableVersionIssue);
    }

    [Test]
    public async Task TheQuerysApprovedVersion_IsNamedToAnUnrestrictedCaller_WithoutAnotherRead()
    {
        var handler = new GetQueryDetailHandler(
            DetailsWithRunnableVersion(7),
            new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict).Object,
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted));

        var details = await handler.Handle(new GetQueryDetailQuery { QueryId = 700 }, CancellationToken.None);

        details.McpToolRunnableVersionNumber.Should().Be(7);
        details.McpToolIssue.Should().BeNull();
    }

    [Test]
    public async Task AiActors_AreThoseOnReadableSources()
    {
        var page = await AnswerAsync<PagedList<AiActorListItem>>(new GetAiActorListQuery());

        page.Items.Select(x => x.ActorId).Should().BeEquivalentTo([510, 530]);
        page.TotalCount.Should().Be(2);
    }

    [Test]
    public async Task DataContracts_AreThoseOnReadableSources()
    {
        var page = await AnswerAsync<PagedList<DataContractData>>(new GetDataContractsQuery());

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([610, 630, 660]);
        page.TotalCount.Should().Be(3);
    }

    [Test]
    public async Task LearnedPatternsAndDocumentationPatches_AreThoseOfReadableProjects()
    {
        var patterns = await AnswerAsync<PagedList<LearnedPatternEntry>>(new GetLearnedPatternsQuery());
        var patches = await AnswerAsync<PagedList<DocumentationPatchEntry>>(new GetDocumentationPatchesQuery());

        patterns.Items.Select(x => x.Id).Should().BeEquivalentTo([801, 803]);
        patterns.TotalCount.Should().Be(2);
        patches.Items.Select(x => x.Id).Should().BeEquivalentTo([901, 903]);
        patches.TotalCount.Should().Be(2);
    }

    private static IQueryService DetailsWithRunnableVersion(int versionNumber)
    {
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.GetQueryDetails(700, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QueryDetailsData { Id = 700, Name = "query 700", McpToolRunnableVersionNumber = versionNumber });

        return queryService.Object;
    }

    private static async Task<T> AnswerAsync<T>(object request, World? world = null)
    {
        var answer = await new ListHandlerHarness(Scope).AnswerAsync(request, (world ?? new World()).Factory());

        return (T)answer!;
    }

    /// <summary>
    /// Projects 1 (open), 2 (restricted), 3 (restricted, the caller a Viewer) and 4 (archived), linking sources 10 and 11,
    /// 11 and 20, 30, and 40; source 50 is unlinked and closed, 60 unlinked and open. Queries read 10 (100), 20 (200), 30
    /// (300), 10 and 30 (310), 40 (400) and 60 (600); 900 has no steps and is the caller's, 901 has none and is another's.
    /// Query 700, now on 10, has versions recorded on 20 (7101), 10 (7102), 30 (7103), none (7107), 10 and 30 (7108) and
    /// 10 and 60 (7109), and legacy versions whose snapshots name 20 (7104) or 10 (7105) or cannot be read (7106).
    /// </summary>
    private sealed class World
    {
        private readonly List<Project> _projects;
        private readonly List<DataSource> _sources;
        private readonly List<ProjectDataSource> _links = [];
        private readonly List<Query> _queries = [];
        private readonly List<QueryStep> _steps = [];
        private readonly List<Subscription> _subscriptions = [];
        private readonly List<QueryExecutionHistory> _runs = [];
        private readonly List<QueryVersion> _versions = [];
        private readonly List<QueryApprovalRequest> _approvals = [];

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
                .ToList();
            Link(1, 10);
            Link(1, 11);
            Link(2, 11);
            Link(2, 20);
            Link(3, 30);
            Link(4, 40);

            AddQuery(100, null, 10);
            AddQuery(200, null, 20);
            AddQuery(300, null, 30);
            AddQuery(310, null, 10, 30);
            AddQuery(400, null, 40);
            AddQuery(600, null, 60);
            AddQuery(900, Caller);
            AddQuery(901, "ext-other");
            AddQuery(700, null, 10);

            foreach (var queryId in new[] { 100, 200, 300, 310, 900 })
            {
                AddSubscription(1000 + queryId, queryId, archived: false);
            }

            AddVersion(7101, 700, [20]);
            AddVersion(7102, 700, [10]);
            AddVersion(7103, 700, [30]);
            AddVersion(7104, 700, null, Snapshot(20));
            AddVersion(7105, 700, null, Snapshot(10));
            AddVersion(7106, 700, null, "not json");
            AddVersion(7107, 700, []);
            AddVersion(7108, 700, [10, 30]);
            AddVersion(7109, 700, [10, 60]);
            AddVersion(9001, 900, []);
            AddVersion(9002, 900, null, "[]");
            AddVersion(9011, 901, []);
            AddVersion(9012, 901, null, "[]");
        }

        public List<QueryStepChangeHistory> History { get; } = [];

        public List<AiActor> Actors { get; } = [];

        public List<DataContract> Contracts { get; } = [];

        public List<McpLearnedPattern> Patterns { get; } = [];

        public List<McpDocumentationPatch> Patches { get; } = [];

        /// <summary>Makes <paramref name="versionId"/> the query's active version, approved: the one its MCP tool runs.</summary>
        public void MakeRunnable(int queryId, int versionId)
        {
            var query = _queries.Single(x => x.Id == queryId);
            var version = _versions.Single(x => x.Id == versionId);
            version.Status = QueryVersionStatus.Active;
            query.ActiveVersionId = versionId;
            query.ActiveVersion = version;
            _approvals.Add(new QueryApprovalRequest { Id = 1, QueryId = queryId, Query = query, QueryVersionId = versionId, QueryVersion = version, Status = ApprovalStatus.Approved });
        }

        public void ArchivedSubscriptions()
        {
            _subscriptions.Clear();
            AddQuery(902, "ext-other");
            AddSubscription(2100, 100, archived: true);
            AddSubscription(2200, 200, archived: true);
            AddSubscription(2901, 901, archived: true);
            AddSubscription(2902, 902, archived: true);
            AddRun(2901, 1, [20]);
            AddRun(2901, 2, [10]);
            AddRun(2901, 3, null);
            AddRun(2902, 4, [10]);
            AddRun(2902, 5, [20]);
        }

        public IDbContextFactory<BeaconContext> Factory()
        {
            var step = _steps.Single(x => x.QueryId == 700);
            History.AddRange(
            [
                new QueryStepChangeHistory { Id = 7001, QueryStepId = step.Id, QueryStep = step, DataSourceId = 20, PreviousSql = "a", NewSql = "b", ChangedAt = DateTime.UtcNow },
                new QueryStepChangeHistory { Id = 7002, QueryStepId = step.Id, QueryStep = step, DataSourceId = 30, PreviousSql = "b", NewSql = "c", ChangedAt = DateTime.UtcNow },
                new QueryStepChangeHistory { Id = 7003, QueryStepId = step.Id, QueryStep = step, DataSourceId = null, PreviousSql = "c", NewSql = "d", ChangedAt = DateTime.UtcNow }
            ]);
            Actors.AddRange(new[] { 10, 20, 30, 50 }.Select(x => new AiActor { Id = 500 + x, Name = $"actor {x}", Instructions = "watch", DataSourceId = x, DataSource = Source(x) }));
            Contracts.AddRange(new[] { 10, 20, 30, 40, 50, 60 }.Select(x => new DataContract { Id = 600 + x, Name = $"contract {x}", SchemaName = "s", TableName = "t", CronExpression = "0 * * * *", DataSourceId = x, DataSource = Source(x) }));
            Patterns.AddRange(new[] { 1, 2, 3, 4 }.Select(x => new McpLearnedPattern { Id = 800 + x, ProjectId = x, DataSourceId = 10, SchemaName = "s", TableName = "t", PatternContent = "p" }));
            Patches.AddRange(new[] { 1, 2, 3, 4 }.Select(x => new McpDocumentationPatch { Id = 900 + x, ProjectId = x, DataSourceId = 10, TargetIdentifier = "t", ProposedContent = "p", Reasoning = "r" }));
            var users = new List<BeaconUser> { new() { Id = 3, ExternalId = Caller, UserName = "ada", IsEnabled = true } };

            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Project)] = RecordingBeaconContext.MemorySet(_projects, saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(_sources, saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(_links, saved),
                        [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(new List<ProjectMember>(), saved),
                        [typeof(Query)] = RecordingBeaconContext.MemorySet(_queries, saved),
                        [typeof(QueryStep)] = RecordingBeaconContext.MemorySet(_steps, saved),
                        [typeof(Subscription)] = RecordingBeaconContext.MemorySet(_subscriptions, saved),
                        [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(_runs, saved),
                        [typeof(QueryStepChangeHistory)] = RecordingBeaconContext.MemorySet(History, saved),
                        [typeof(AiActor)] = RecordingBeaconContext.MemorySet(Actors, saved),
                        [typeof(DataContract)] = RecordingBeaconContext.MemorySet(Contracts, saved),
                        [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(new List<DataQualityScore>(), saved),
                        [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(Patterns, saved),
                        [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(Patches, saved),
                        [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved),
                        [typeof(QueryVersion)] = RecordingBeaconContext.MemorySet(_versions, saved),
                        [typeof(QueryApprovalRequest)] = RecordingBeaconContext.MemorySet(_approvals, saved)
                    },
                    saved,
                    new SqlCapture()));

            return factory.Object;
        }

        public DataSource Source(int id) => _sources.Single(x => x.Id == id);

        private void Link(int projectId, int dataSourceId)
        {
            var link = new ProjectDataSource { ProjectId = projectId, DataSourceId = dataSourceId, Project = _projects.Single(x => x.Id == projectId), DataSource = Source(dataSourceId) };
            _links.Add(link);
            link.Project.DataSources.Add(link);
        }

        private void AddQuery(int id, string? ownerUserId, params int[] dataSourceIds)
        {
            var query = new Query { Id = id, Name = $"query {id}", OwnerUserId = ownerUserId, CreatedTime = DateTime.UtcNow.AddMinutes(-id) };
            var order = 1;
            foreach (var dataSourceId in dataSourceIds)
            {
                var step = new QueryStep
                {
                    Id = (id * 10) + order,
                    QueryId = id,
                    Query = query,
                    DataSourceId = dataSourceId,
                    DataSource = Source(dataSourceId),
                    StepOrder = order++,
                    SqlValue = "select 1"
                };
                query.Steps.Add(step);
                _steps.Add(step);
                step.DataSource.QuerySteps.Add(step);
            }

            _queries.Add(query);
        }

        private static string Snapshot(int dataSourceId)
        {
            return $$"""[{"stepOrder":1,"sqlValue":"select 1","dataSourceId":{{dataSourceId}},"dataSourceName":"source {{dataSourceId}}"}]""";
        }

        private void AddVersion(int id, int queryId, int[]? dataSourceIds, string? stepsJson = null)
        {
            var query = _queries.Single(x => x.Id == queryId);
            _versions.Add(new QueryVersion
            {
                Id = id,
                QueryId = queryId,
                Query = query,
                VersionNumber = id % 100,
                Status = QueryVersionStatus.Archived,
                Name = $"version {id}",
                StepsJson = stepsJson ?? "[]",
                DataSourceIds = dataSourceIds
            });
        }

        private void AddSubscription(int id, int queryId, bool archived)
        {
            var query = _queries.Single(x => x.Id == queryId);
            var subscription = new Subscription { Id = id, QueryId = queryId, Query = query, CronExpression = "0 * * * *", ArchivedTime = archived ? DateTime.UtcNow : null };
            query.Subscriptions.Add(subscription);
            _subscriptions.Add(subscription);
        }

        private void AddRun(int subscriptionId, int id, int[]? dataSourceIds)
        {
            _runs.Add(new QueryExecutionHistory
            {
                Id = id,
                SubscriptionId = subscriptionId,
                Subscription = _subscriptions.Single(x => x.Id == subscriptionId),
                ResultCount = 0,
                CompiledSql = "select 1",
                NotificationStatus = NotificationStatus.NotificationSent,
                ExecutionTimeMs = 1,
                DataSourceIds = dataSourceIds
            });
        }
    }
}
