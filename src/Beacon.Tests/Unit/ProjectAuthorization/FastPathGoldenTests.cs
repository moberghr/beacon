using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Handlers.ExecutionLog;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Handlers.ControlTower;
using Beacon.Core.Handlers.DataQuality.GetDataContracts;
using Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Notifications;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.QueryFolders;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Handlers.Tasks;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The fast path changes nothing: while no project is restricted or archived and every data source is openly
/// reachable, each list handler sends exactly the SQL it sent before project access existed, recorded in
/// <c>FastPathGolden/*.sql</c>; the exceptions are that rows recorded without a project stay an Admin's (the learning
/// stats count only signals with a project) and that only an Admin's data-source list counts migration jobs. Set
/// <c>BEACON_RECORD_FASTPATH_GOLDEN</c> to the golden directory to re-record.
/// </summary>
[TestFixture]
public class FastPathGoldenTests
{
    private const string RecordVariable = "BEACON_RECORD_FASTPATH_GOLDEN";

    private const string Separator = "\n;\n";

    public static IEnumerable<TestCaseData> ListRequests()
    {
        yield return Case("GetProjects", new GetProjectsQuery());
        yield return Case("GetQueries", new GetQueriesQuery());
        yield return Case("GetQueryChangeHistory", new GetQueryChangeHistoryQuery { QueryId = 5 });
        yield return Case("GetPendingApprovals", new GetPendingApprovalsQuery());
        yield return Case("GetQueryFolders", new GetQueryFoldersQuery());
        yield return Case("ListQueryFolders", new ListQueryFoldersQuery());
        yield return Case("GetDataSources", new GetDataSourcesQuery());
        yield return Case("GetSubscriptions", new GetSubscriptionsQuery());
        yield return Case("GetNotifications", new GetNotificationsQuery());
        yield return Case("GetTasks", new GetTasksQuery());
        yield return Case("GetControlTowerHealth", new GetControlTowerHealthQuery());
        yield return Case("GetControlTowerStatistics", new GetControlTowerStatisticsQuery());
        yield return Case("GetHomeTrends", new GetHomeTrendsQuery());
        yield return Case("GetHomeActivity", new GetHomeActivityQuery());
        yield return Case("GetHomeTaskSummary", new GetHomeTaskSummaryQuery());
        yield return Case("GetExecutionUptime", new GetExecutionUptimeQuery());
        yield return Case("GetDataQualityOverview", new GetDataQualityOverviewQuery());
        yield return Case("ListDataQualityOverview", new ListDataQualityOverviewQuery());
        yield return Case("GetDataContracts", new GetDataContractsQuery());
        yield return Case("GetAiActorList", new GetAiActorListQuery());
        yield return Case("GetPendingPlanCount", new GetPendingPlanCountQuery());
        yield return Case("GetLearnedPatterns", new GetLearnedPatternsQuery());
        yield return Case("GetDocumentationPatches", new GetDocumentationPatchesQuery());
        yield return Case("GetLearningStats", new GetLearningStatsQuery());
        yield return Case("GetExecutionLog", new GetExecutionLogQuery { From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc) });
        yield return Case("ExportExecutionLog", new ExportExecutionLogQuery { From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc) });
        yield return Case("ListEmbedGrants", new ListEmbedGrantsQuery());
    }

    [TestCaseSource(nameof(ListRequests))]
    public async Task ListHandler_OnTheFastPath_SendsTheRecordedSql(string name, object request)
    {
        var commands = await new ListHandlerHarness(TestProjectAccess.FastPath).SendAsync(request);
        var sql = Normalize(string.Join(Separator, commands));

        commands.Should().NotBeEmpty($"{name} reads the database");

        var recordInto = Environment.GetEnvironmentVariable(RecordVariable);
        if (!string.IsNullOrWhiteSpace(recordInto))
        {
            await File.WriteAllTextAsync(Path.Combine(recordInto, $"{name}.sql"), sql);
            Assert.Ignore($"Recorded {name}.sql");
        }

        var golden = Path.Combine(TestContext.CurrentContext.TestDirectory, "Unit", "ProjectAuthorization", "FastPathGolden", $"{name}.sql");
        File.Exists(golden).Should().BeTrue($"{name} has a recorded baseline");
        sql.Should().Be(Normalize(await File.ReadAllTextAsync(golden)), $"{name}'s SQL is unchanged on the fast path");
    }

    private static TestCaseData Case(string name, object request)
    {
        return new TestCaseData(name, request).SetName($"ListHandler_OnTheFastPath_SendsTheRecordedSql({name})");
    }

    private static string Normalize(string sql)
    {
        return sql.Replace("\r\n", "\n").Trim();
    }
}
