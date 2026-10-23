using Beacon.Core.Authorization;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Handlers.ControlTower;
using Beacon.Core.Handlers.DataQuality.GetDataContracts;
using Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Notifications;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.QueryFolders;
using Beacon.Core.Handlers.QueryVersions;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Handlers.Tasks;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Every list and aggregate request (<see cref="IProjectFilteredRequest"/>) filters in the SQL it sends: under a scope
/// that does not reach every project, each one's SQL carries the scope predicate (a project-id list parameter or the
/// project/data-source link table) and differs from what it sends on the fast path.
/// </summary>
[TestFixture]
public class ProjectListScopeShapeTests
{
    /// <summary>A caller reading in projects 1 and 2 and the openly reachable sources, except data source 9.</summary>
    private static readonly ProjectAccessScope Restricted = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Viewer, [2] = ProjectRole.Editor },
        includesOpen: true,
        notOpen: [9]);

    private static readonly IReadOnlyDictionary<Type, object> Scenarios = new Dictionary<Type, object>
    {
        [typeof(GetProjectsQuery)] = new GetProjectsQuery(),
        [typeof(GetQueriesQuery)] = new GetQueriesQuery(),
        [typeof(GetQueryChangeHistoryQuery)] = new GetQueryChangeHistoryQuery { QueryId = 5 },
        [typeof(GetQueryVersionsQuery)] = new GetQueryVersionsQuery { QueryId = 5 },
        [typeof(GetPendingApprovalsQuery)] = new GetPendingApprovalsQuery(),
        [typeof(GetChangeApprovalsQuery)] = new GetChangeApprovalsQuery(),
        [typeof(GetQueryFoldersQuery)] = new GetQueryFoldersQuery(),
        [typeof(GetDataSourcesQuery)] = new GetDataSourcesQuery(),
        [typeof(GetSubscriptionsQuery)] = new GetSubscriptionsQuery(),
        [typeof(GetNotificationsQuery)] = new GetNotificationsQuery(),
        [typeof(GetTasksQuery)] = new GetTasksQuery(),
        [typeof(GetControlTowerHealthQuery)] = new GetControlTowerHealthQuery(),
        [typeof(GetControlTowerStatisticsQuery)] = new GetControlTowerStatisticsQuery(),
        [typeof(GetHomeTrendsQuery)] = new GetHomeTrendsQuery(),
        [typeof(GetHomeActivityQuery)] = new GetHomeActivityQuery(),
        [typeof(GetHomeTaskSummaryQuery)] = new GetHomeTaskSummaryQuery(),
        [typeof(GetExecutionUptimeQuery)] = new GetExecutionUptimeQuery(),
        [typeof(GetDataQualityOverviewQuery)] = new GetDataQualityOverviewQuery(),
        [typeof(GetDataContractsQuery)] = new GetDataContractsQuery(),
        [typeof(GetAiActorListQuery)] = new GetAiActorListQuery(),
        [typeof(GetLearnedPatternsQuery)] = new GetLearnedPatternsQuery(),
        [typeof(GetDocumentationPatchesQuery)] = new GetDocumentationPatchesQuery(),
        [typeof(GetLearningStatsQuery)] = new GetLearningStatsQuery()
    };

    public static IEnumerable<TestCaseData> FilteredRequests() =>
        Scenarios.Select(x => new TestCaseData(x.Value).SetName($"FilteredRequest_SendsTheScopePredicate({x.Key.Name})"));

    [Test]
    public void EveryFilteredRequest_HasAScopeShapeTest()
    {
        var filtered = BeaconApiRoutes.RequestTypes()
            .Where(x => typeof(IProjectFilteredRequest).IsAssignableFrom(x))
            .ToList();

        filtered.Should().NotBeEmpty();
        filtered.Where(x => !Scenarios.ContainsKey(x)).Select(x => x.Name).Should().BeEmpty("each filtered request has a scenario here");
    }

    [TestCaseSource(nameof(FilteredRequests))]
    public async Task FilteredRequest_SendsTheScopePredicate(object request)
    {
        var restricted = string.Join("\n;\n", await new ListHandlerHarness(Restricted).SendAsync(request));
        var fastPath = string.Join("\n;\n", await new ListHandlerHarness(TestProjectAccess.FastPath).SendAsync(request));

        restricted.Should().MatchRegex(
            "project_data_sources|project_id = ANY \\(@|\\.id = ANY \\(@",
            "a restricted caller's list is filtered in the SQL");
        restricted.Should().NotBe(fastPath, "the fast path adds no predicate");
    }

    // A caller who is no Approver asking for the LLM connection's change requests: the list is scoped in the SQL to the
    // migration jobs and data contracts it reads, so no LLM connection request can match.
    [Test]
    public async Task TheLlmConnectionRequests_AreUnreachable_ForARestrictedReaderWhoIsNoApprover()
    {
        var capture = await new ListHandlerHarness(Restricted).CaptureAsync(new GetChangeApprovalsQuery { Kind = Beacon.Core.Data.Enums.ChangeApprovalKind.LlmConnection });

        capture.Commands.Should().NotBeEmpty().And.OnlyContain(x => x.Contains("project_data_sources", StringComparison.Ordinal));
        capture.Commands.Should().OnlyContain(x => x.Contains("c.kind = 1", StringComparison.Ordinal) && x.Contains("c.kind = 2", StringComparison.Ordinal));
        capture.Commands.Should().NotContain(x => x.Contains("c.kind = 5", StringComparison.Ordinal), "only a live Admin reads them, never through a project");
        capture.Commands.Should().NotContain(x => x.Contains("c.kind = 4", StringComparison.Ordinal) || x.Contains("c.kind = 3", StringComparison.Ordinal), "a connection's or a recipient's change is an Approver's to read");
    }

    // The same reader asking for data-source connection requests reaches none of them either.
    [Test]
    public async Task TheDataSourceConnectionRequests_AreUnreachable_ForARestrictedReaderWhoIsNoApprover()
    {
        var capture = await new ListHandlerHarness(Restricted).CaptureAsync(new GetChangeApprovalsQuery { Kind = Beacon.Core.Data.Enums.ChangeApprovalKind.DataSourceConnection });

        capture.Commands.Should().NotBeEmpty().And.OnlyContain(x => x.Contains("c.kind = 1", StringComparison.Ordinal) && x.Contains("c.kind = 2", StringComparison.Ordinal));
        capture.Commands.Should().NotContain(x => x.Contains("c.kind = 4", StringComparison.Ordinal), "an Approver's to read, never through a project");
    }
}
