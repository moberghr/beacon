using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Notifications;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// The project access queries translate on both providers (no database; <c>ToQueryString()</c> only): the evaluator's
/// one snapshot command, the run filter with the openly reachable and the legacy-run arms, the link read and the
/// conditional membership change that keeps a project's last active ProjectAdmin.
/// </summary>
[TestFixture]
public class ProjectAccessQueryTranslationTests
{
    [Test]
    public void ProjectAccessSnapshot_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = ProjectAccessSnapshot.Query(context, 7).ToQueryString();

        sql.Should().Contain("UNION ALL", "projects and closed data sources are read in one command");
        sql.Should().Contain("FROM beacon.projects AS p");
        sql.Should().Contain("project_members").And.Contain("user_id = @");
        sql.Should().Contain("open_when_unlinked");
        sql.Should().Contain("requires_membership");
        sql.Should().NotContain("archived_time IS NULL\n", "archived projects and data sources are read too");
    }

    [Test]
    public void ProjectAccessSnapshot_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = ProjectAccessSnapshot.Query(context, 7).ToQueryString();

        sql.Should().Contain("UNION ALL");
        sql.Should().Contain("[beacon].[ProjectMembers]");
        sql.Should().Contain("[OpenWhenUnlinked]");
        sql.Should().Contain("[RequiresMembership]");
    }

    [Test]
    public void ProjectAccessSnapshot_SqlServer_ReadsArchivedAndRestrictedProjectsInTheNotOpenBranch_AndTheCallersMembership()
    {
        using var context = SqlServerTestContext.Create();

        var sql = ProjectAccessSnapshot.Query(context, 7).ToQueryString();
        var notOpenBranch = sql[sql.IndexOf("UNION ALL", StringComparison.Ordinal)..];

        notOpenBranch.Should().Contain("[RequiresMembership]", "a source linked only to restricted projects is not open");
        notOpenBranch.Should().Contain("[ArchivedTime] IS NULL", "a source linked only to archived projects is not open");
        notOpenBranch.Should().Contain("[OpenWhenUnlinked]");
        sql.Should().MatchRegex(@"\[ProjectId\] = \[\w+\]\.\[Id\]", "the membership row is the project's");
        sql.Should().MatchRegex(@"\[UserId\] = @", "and the caller's");
    }

    [Test]
    public void ProjectAccessSnapshot_WithoutAMember_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = ProjectAccessSnapshot.Query(context, null).ToQueryString();

        sql.Should().Contain("UNION ALL");
    }

    [Test]
    public void StoredRunsReadableWithinProjectSet_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var scope = StoredRunScope.From(new ProjectSet([1, 2], true, [9]));

        var sql = context.QueryExecutionHistory
            .WhereReadableWithin(context, scope)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("data_source_ids IS NOT NULL", "recorded runs are read by their recorded sources");
        sql.Should().Contain("data_source_ids IS NULL", "legacy runs are read by their subscription's current query");
        sql.Should().Contain("project_data_sources");
        sql.Should().Contain("query_steps");
    }

    [Test]
    public void StoredRunsReadableWithinProjectSet_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();
        var scope = StoredRunScope.From(new ProjectSet([1, 2], true, [9]));

        var sql = context.QueryExecutionHistory
            .WhereReadableWithin(context, scope)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("OPENJSON", "recorded sources are a JSON column on SQL Server");
        sql.Should().Contain("[QuerySteps]");
    }

    [Test]
    public void StoredRunsWithinAProjectList_KeepTheRestrictedShape_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.QueryExecutionHistory
            .WhereReadableWithin(context, StoredRunScope.From(new ProjectSet([1], false, []) { IsConfinedToList = true }))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().NotContain("query_steps", "a caller confined to a project list never reads legacy runs");
        sql.Should().Contain("cardinality(", "a run without recorded sources is never readable under a list");
    }

    [Test]
    public void StoredRunsOfAMemberWhenMembershipIsRequired_KeepTheLegacyArm_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.QueryExecutionHistory
            .WhereReadableWithin(context, StoredRunScope.From(new ProjectSet([1], false, [])))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("data_source_ids IS NULL", "legacy runs are read by their subscription's live query");
        sql.Should().Contain("query_steps");
        sql.Should().MatchRegex(@"q\d\.archived_time IS NULL", "a legacy run on an archived query is an Admin's");
        sql.Should().NotContain("<> ALL", "no source is openly reachable");
    }

    [Test]
    public void StoredRunsOfAMemberWhenMembershipIsRequired_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = context.QueryExecutionHistory
            .WhereReadableWithin(context, StoredRunScope.From(new ProjectSet([1], false, [])))
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("[DataSourceIds] IS NULL");
        sql.Should().Contain("[QuerySteps]");
        sql.Should().Contain("[ArchivedTime] IS NULL");
    }

    [Test]
    public void HoldersOfEverySource_Translate()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = ProjectSourceFootprint.HoldersOf(context, [10, 20]).ToQueryString();

        sql.Should().Contain("GROUP BY p.project_id").And.Contain("HAVING count(DISTINCT p.data_source_id)");
        sql.Should().NotContain("projects AS", "archived projects hold their sources too; the caller's set decides");
    }

    [Test]
    public void HoldersOfEverySource_SqlServer_Translate()
    {
        using var context = SqlServerTestContext.Create();

        var sql = ProjectSourceFootprint.HoldersOf(context, [10, 20]).ToQueryString();

        sql.Should().Contain("GROUP BY [p].[ProjectId]").And.Contain("HAVING COUNT(DISTINCT ([p].[DataSourceId])) = @count");
    }

    [Test]
    public void ProjectsHoldingEverySource_Translate()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = ProjectSourceFootprint.ProjectsHoldingAll(context, [1, 2], [10, 20]).ToQueryString();

        sql.Should().Contain("GROUP BY").And.Contain("HAVING").And.Contain("count(DISTINCT");
    }

    [Test]
    public void ProjectsHoldingEverySource_SqlServer_Translate()
    {
        using var context = SqlServerTestContext.Create();

        var sql = ProjectSourceFootprint.ProjectsHoldingAll(context, [1, 2], [10, 20]).ToQueryString();

        sql.Should().Contain("GROUP BY").And.Contain("HAVING").And.Contain("COUNT(DISTINCT");
    }

    [Test]
    public void LinkCandidateLinks_Translate()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = ProjectDataSourceLinks.LinksQuery(context, [3, 4]).ToQueryString();

        sql.Should().Contain("project_data_sources");
        sql.Should().Contain("requires_membership");
        sql.Should().NotContain("archived_time IS NULL AND", "links to archived projects are read, as not open");
    }

    [Test]
    public void LinkCandidateLinks_SqlServer_Translate()
    {
        using var context = SqlServerTestContext.Create();

        var sql = ProjectDataSourceLinks.LinksQuery(context, [3, 4]).ToQueryString();

        sql.Should().Contain("[ProjectDataSources]");
    }

    [Test]
    public void MembershipConditionalChange_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.ProjectMembers
            .Where(x => x.ProjectId == 5)
            .Where(x => x.UserId == 8)
            .WhereChangeKeepsAProjectAdmin(context, 5, 8, mayLeaveNoAdmin: false)
            .ToQueryString();

        sql.Should().Contain("EXISTS", "the last-ProjectAdmin rule is part of the one statement");
        sql.Should().Contain("is_enabled");
        sql.Should().Contain("user_id <> @");
    }

    [Test]
    public void MembershipConditionalChange_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = context.ProjectMembers
            .Where(x => x.ProjectId == 5)
            .Where(x => x.UserId == 8)
            .WhereChangeKeepsAProjectAdmin(context, 5, 8, mayLeaveNoAdmin: false)
            .ToQueryString();

        sql.Should().Contain("EXISTS");
        sql.Should().Contain("[IsEnabled]");
    }

    [Test]
    public async Task ActorLookup_ReadsLiveRolesInTheSameCommand_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        await BeaconActorAccessor.ResolveAsync(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "ext-ana")],
                "Cookies")),
            capture.Factory(),
            CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("user_roles").And.Contain("max(");
        sql.Should().Contain("is_super_admin");
    }
}
