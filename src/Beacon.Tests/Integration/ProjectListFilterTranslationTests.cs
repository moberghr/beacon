using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// The list filters translate on both providers (no database; <c>ToQueryString()</c> only), keep a fixed shape whatever
/// the number of projects (the ids are one parameter), and add nothing when the set reaches every project.
/// </summary>
[TestFixture]
public class ProjectListFilterTranslationTests
{
    private static readonly ProjectSet Restricted = new([1, 2], true, [9]);

    private static readonly ProjectSet ListOnly = new([1, 2], false, []);

    public static IEnumerable<Func<BeaconContext>> Contexts()
    {
        yield return NpgsqlTestContext.Create;
        yield return SqlServerTestContext.Create;
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereProjectIn_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.Projects
            .WhereProjectIn(Restricted, x => x.Id)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)(= ANY|IN \\(|OPENJSON)");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereProjectIn_NullableProject_IsAdminOnly_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.McpQuerySignals
            .WhereProjectIn(Restricted, x => x.ProjectId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)IS NOT NULL");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereProjectIn_NullableProject_StaysAdminOnlyOnTheFastPath_Translates(Func<BeaconContext> create)
    {
        using var context = create();
        var baseline = context.McpQuerySignals.Select(x => x.Id).ToQueryString();

        var fastPath = context.McpQuerySignals
            .WhereProjectIn(ProjectSet.All, x => x.ProjectId)
            .Select(x => x.Id)
            .ToQueryString();
        var unrestricted = context.McpQuerySignals
            .WhereProjectIn(ProjectSet.Unrestricted, x => x.ProjectId)
            .Select(x => x.Id)
            .ToQueryString();

        fastPath.Should().MatchRegex("(?i)IS NOT NULL", "a signal recorded without a project is an Admin's even where everything is open");
        unrestricted.Should().Be(baseline);
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereSourceAccessible_NullableSource_StaysAdminOnlyOnTheFastPath_Translates(Func<BeaconContext> create)
    {
        using var context = create();
        var baseline = context.McpGlossaryTerms.Select(x => x.Id).ToQueryString();

        var fastPath = context.McpGlossaryTerms
            .WhereSourceAccessible(context, ProjectSet.All, x => x.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();
        var unrestricted = context.McpGlossaryTerms
            .WhereSourceAccessible(context, ProjectSet.Unrestricted, x => x.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();

        fastPath.Should().MatchRegex("(?i)IS NOT NULL");
        unrestricted.Should().Be(baseline);
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereSourceAccessible_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.DataContracts
            .WhereSourceAccessible(context, Restricted, x => x.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)EXISTS");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereSourceAccessible_NullableSource_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.McpGlossaryTerms
            .WhereSourceAccessible(context, ListOnly, x => x.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)IS NOT NULL");
        sql.Should().MatchRegex("(?i)EXISTS");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereAllSourcesInOneProject_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.Queries
            .WhereAllSourcesInOneProject(context, Restricted, x => x.Steps.Select(y => y.DataSourceId))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)query_steps|QuerySteps");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereAllSourcesInOneProject_WithinAProjectList_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.Subscriptions
            .WhereAllSourcesInOneProject(context, ListOnly, x => x.Query.Steps.Select(y => y.DataSourceId))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)EXISTS");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereAllSourcesInOneProject_WithTheQueryOwner_Translates(Func<BeaconContext> create)
    {
        using var context = create();
        var ownersSet = Restricted with { QueryOwnerUserId = "ext-ada" };

        var sql = context.Subscriptions
            .WhereAllSourcesInOneProject(context, ownersSet, x => x.Query.Steps.Select(y => y.DataSourceId), x => x.Query.OwnerUserId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)owner_user_id|\\[OwnerUserId\\]");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
        Statement(sql).Should().Contain("@owner").And.NotContain("ext-ada", "the owner is a parameter");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereAllSourcesInOneProject_WithoutAQueryOwner_IsTheSourceRuleAlone_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var plain = context.Queries
            .WhereAllSourcesInOneProject(context, Restricted, x => x.Steps.Select(y => y.DataSourceId))
            .Select(x => x.Id)
            .ToQueryString();
        var withOwnerSelector = context.Queries
            .WhereAllSourcesInOneProject(context, Restricted, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId)
            .Select(x => x.Id)
            .ToQueryString();

        withOwnerSelector.Should().Be(plain);
        plain.Should().NotMatchRegex("(?i)owner_user_id|\\[OwnerUserId\\]");
    }

    [TestCaseSource(nameof(Contexts))]
    public void ArchivedSubscriptions_ByLiveOrLastRunSources_Translate(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.Subscriptions
            .IgnoreQueryFilters()
            .Where(x => x.ArchivedTime != null)
            .Where(GetSubscriptionsHandler.ArchivedReadable(context, Restricted))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)query_execution_history|QueryExecutionHistory", "a subscription whose query has no steps left is read by its last run");
        sql.Should().MatchRegex("(?i)LIMIT 1|LIMIT @|TOP\\(1\\)|TOP\\(@");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereVersionSourcesAccessible_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.QueryApprovalRequests
            .WhereVersionSourcesAccessible(context, Restricted, x => x.QueryVersion.DataSourceIds)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)IS NULL");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void VersionListFilter_WithTheQueryOwner_Translates(Func<BeaconContext> create)
    {
        using var context = create();
        var ownersSet = Restricted with { QueryOwnerUserId = "ext-ada" };
        var unreadableLegacy = new List<int> { 10, 11 };

        var sql = context.QueryVersions
            .Where(x => x.QueryId == 5)
            .WhereVersionSourcesAccessible(context, ownersSet, x => x.DataSourceIds, x => x.Query.OwnerUserId)
            .Where(x => !unreadableLegacy.Contains(x.Id))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)IS NULL", "a version recorded before sources were kept is decided per row");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
        sql.Should().MatchRegex("(?i)owner_user_id|\\[OwnerUserId\\]", "a version recorded without sources is its query owner's");
        Statement(sql).Should().Contain("@owner").And.NotContain("ext-ada", "the owner is a parameter");
    }

    [TestCaseSource(nameof(Contexts))]
    public void WhereVersionSourcesAccessible_WithoutAQueryOwner_IsTheSourceRuleAlone_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var plain = context.QueryVersions
            .WhereVersionSourcesAccessible(context, Restricted, x => x.DataSourceIds)
            .Select(x => x.Id)
            .ToQueryString();
        var withOwnerSelector = context.QueryVersions
            .WhereVersionSourcesAccessible(context, Restricted, x => x.DataSourceIds, x => x.Query.OwnerUserId)
            .Select(x => x.Id)
            .ToQueryString();

        withOwnerSelector.Should().Be(plain);
        plain.Should().NotMatchRegex("(?i)owner_user_id|\\[OwnerUserId\\]");
    }

    [TestCaseSource(nameof(Contexts))]
    public void QueryChangeHistoryPerRowSource_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.QueryStepChangeHistory
            .Where(x => x.QueryStep.QueryId == 5)
            .WhereSourceAccessible(context, Restricted, x => x.DataSourceId ?? x.QueryStep.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)query_steps|QuerySteps");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
        sql.Should().MatchRegex(@"(?i)COALESCE\([\[\]\w.]*data_?source_?id[\]]*, [\[\]\w.]*data_?source_?id[\]]*\)", "a change is read by the source it recorded, else its step's current one");
    }

    [TestCaseSource(nameof(Contexts))]
    public void PendingApprovalsFilter_Translates(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = context.QueryApprovalRequests
            .Where(x => x.Status == ApprovalStatus.Pending)
            .WhereAllSourcesInOneProject(context, Restricted, x => x.Query.Steps.Select(y => y.DataSourceId))
            .WhereVersionSourcesAccessible(context, Restricted, x => x.QueryVersion.DataSourceIds)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)query_versions|QueryVersions");
        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void FolderQueryCounts_Translate(Func<BeaconContext> create)
    {
        using var context = create();
        var readableQuery = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<Query>(context, Restricted, x => x.Steps.Select(y => y.DataSourceId));

        var sql = context.QueryFolders
            .Select(x =>
                new
                {
                    x.Id,
                    QueryCount = x.Queries
                        .AsQueryable()
                        .Count(readableQuery)
                })
            .ToQueryString();

        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void ControlTowerStatistics_Translate(Func<BeaconContext> create)
    {
        using var context = create();

        var sql = ControlTowerService
            .BuildSubscriptionStatsQuery(context, new GetControlTowerDataRequest { Readable = Restricted }, DateTime.UtcNow)
            .ToQueryString();

        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void DataSourceProjectChips_Translate(Func<BeaconContext> create)
    {
        using var context = create();
        var ids = Restricted.Ids!.ToList();

        var sql = context.DataSources
            .WhereSourceAccessible(context, Restricted, x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    Projects = context.ProjectDataSources
                        .Where(y => y.DataSourceId == x.Id)
                        .Where(y => ids.Contains(y.ProjectId))
                        .Select(y => y.Project.Name)
                        .ToList(),
                    OtherProjectCount = context.Projects
                        .Where(y => y.DataSources.Any(z => z.DataSourceId == x.Id))
                        .Count(y => !ids.Contains(y.Id))
                })
            .ToQueryString();

        sql.Should().MatchRegex("(?i)project_data_sources|ProjectDataSources");
    }

    [TestCaseSource(nameof(Contexts))]
    public void ListFilters_HaveAFixedShape_WhateverTheNumberOfProjects(Func<BeaconContext> create)
    {
        using var context = create();
        var one = new ProjectSet([1], true, [9]);
        var fifty = new ProjectSet(Enumerable.Range(1, 50).ToList(), true, Enumerable.Range(100, 50).ToList());

        var withOne = context.Queries
            .WhereAllSourcesInOneProject(context, one, x => x.Steps.Select(y => y.DataSourceId))
            .Select(x => x.Id)
            .ToQueryString();
        var withFifty = context.Queries
            .WhereAllSourcesInOneProject(context, fifty, x => x.Steps.Select(y => y.DataSourceId))
            .Select(x => x.Id)
            .ToQueryString();

        Shape(withFifty).Should().Be(Shape(withOne), "the project ids are one parameter, not one subquery each");
        CountOf(withFifty, "EXISTS").Should().Be(CountOf(withOne, "EXISTS"));
    }

    [TestCaseSource(nameof(Contexts))]
    public void ListFilters_AddNothing_WhenTheSetReachesEveryProject(Func<BeaconContext> create)
    {
        using var context = create();
        var baseline = context.Queries.Select(x => x.Id).ToQueryString();

        context.Queries
            .WhereAllSourcesInOneProject(context, ProjectSet.All, x => x.Steps.Select(y => y.DataSourceId))
            .WhereAllSourcesInOneProject(context, ProjectSet.All, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId)
            .WhereSourceAccessible(context, ProjectSet.All, x => x.Id)
            .WhereProjectIn(ProjectSet.All, x => x.Id)
            .Select(x => x.Id)
            .ToQueryString()
            .Should().Be(baseline);
    }

    [Test]
    public void ResolverQueries_Translate()
    {
        using var context = NpgsqlTestContext.Create();
        var resolver = new ProjectResourceResolver();

        foreach (var kind in Enum.GetValues<ProjectResourceKind>())
        {
            var capture = new SqlCapture();
            using var captured = (BeaconContext)capture.Factory().CreateDbContext();
            var resource = kind == ProjectResourceKind.DataSources ? ProjectResource.DataSources([1, 2]) : Of(kind, 5);

            var act = () => resolver.ResolveAsync(captured, resource, CancellationToken.None);

            act.Should().ThrowAsync<SqlCapturedException>().GetAwaiter().GetResult();
            capture.Commands.Should().ContainSingle(kind.ToString());
        }
    }

    private static ProjectResource Of(ProjectResourceKind kind, int id)
    {
        var factory = typeof(ProjectResource).GetMethod(kind.ToString())!;

        return (ProjectResource)factory.Invoke(null, [id])!;
    }

    // The statement without its parameter values: PostgreSQL lists them as comments, SQL Server as declarations.
    private static string Statement(string sql)
    {
        return string.Join(
            "\n",
            sql
                .Split('\n')
                .Where(x => !x.StartsWith("--", StringComparison.Ordinal))
                .Where(x => !x.StartsWith("DECLARE ", StringComparison.Ordinal)));
    }

    // The statement with every parameter, and every list of parameters, as one placeholder: SQL Server expands a list
    // into one parameter per value (an IN list, or a VALUES row each), and compares a list of one with "=".
    private static string Shape(string sql)
    {
        var shape = System.Text.RegularExpressions.Regex.Replace(Statement(sql), "@\\w+", "@p");
        shape = System.Text.RegularExpressions.Regex.Replace(shape, "\\(@p\\)(, \\(@p\\))+", "(@p)");
        shape = System.Text.RegularExpressions.Regex.Replace(shape, "@p(, @p)+", "@p");

        return shape.Replace("IN (@p)", "= @p");
    }

    private static int CountOf(string sql, string token)
    {
        return System.Text.RegularExpressions.Regex.Matches(sql, token).Count;
    }
}
