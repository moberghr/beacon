using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The last-ProjectAdmin rule: a ProjectAdmin's row may be demoted or removed only while another user is an effective
/// ProjectAdmin of the same project, meaning enabled, not archived, and holding a global role of Editor or above (or
/// super admin). The predicate is pinned in full, in order, on both providers, and its outcome is checked row by row.
/// </summary>
[TestFixture]
public class ProjectMembershipRulesTests
{
    private const int ProjectId = 1;
    private const int TargetId = 8;
    private const int OtherId = 9;

    [Test]
    public void LastProjectAdminCondition_Translates_OnPostgreSql()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = Changeable(context).ToQueryString();

        sql.Should().MatchRegex(
            @"WHERE p\.project_id = 1 AND p\.user_id = 8 AND \(p\.role <> 3 OR EXISTS \(",
            "a row that is not a ProjectAdmin's may always change");
        sql.Should().MatchRegex(
            @"WHERE (\w+)\.project_id = @projectId AND \1\.user_id <> @userId AND \1\.role = 3 AND (\w+)\.is_enabled AND \2\.archived_time IS NULL AND \(\2\.is_super_admin OR EXISTS \(",
            "another ProjectAdmin of the same project, enabled and not archived, a super admin or");
        sql.Should().MatchRegex(@"INNER JOIN beacon\.roles AS \w+ ON \w+\.role_id = \w+\.id\s+WHERE \w+\.id = \w+\.user_id AND \w+\.level >= 2\)", "holding a global role of Editor or above");
    }

    [Test]
    public void LastProjectAdminCondition_Translates_OnSqlServer()
    {
        using var context = SqlServerTestContext.Create();

        var sql = Changeable(context).ToQueryString();

        sql.Should().Contain("WHERE [p].[ProjectId] = 1 AND [p].[UserId] = 8 AND ([p].[Role] <> 3 OR EXISTS (");
        sql.Should().MatchRegex(
            @"WHERE \[(\w+)\]\.\[ProjectId\] = @projectId AND \[\1\]\.\[UserId\] <> @userId AND \[\1\]\.\[Role\] = 3 AND \[(\w+)\]\.\[IsEnabled\] = CAST\(1 AS bit\) AND \[\2\]\.\[ArchivedTime\] IS NULL AND \(\[\2\]\.\[IsSuperAdmin\] = CAST\(1 AS bit\) OR EXISTS \(");
        sql.Should().MatchRegex(@"\[\w+\]\.\[Level\] >= 2\)");
    }

    [Test]
    public void AnAdmin_ChangesWithoutTheCondition()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.ProjectMembers
            .Where(x => x.ProjectId == ProjectId)
            .WhereChangeKeepsAProjectAdmin(context, ProjectId, TargetId, mayLeaveNoAdmin: true)
            .ToQueryString();

        sql.Should().NotContain("EXISTS");
    }

    public static IEnumerable<TestCaseData> Outcomes()
    {
        yield return Case("the last ProjectAdmin", Admin(TargetId), changes: false);
        yield return Case("another ProjectAdmin, disabled", Admin(TargetId), changes: false, Admin(OtherId, enabled: false));
        yield return Case("another ProjectAdmin, archived", Admin(TargetId), changes: false, Admin(OtherId, archived: true));
        yield return Case("a ProjectAdmin of another project only", Admin(TargetId), changes: false, Admin(OtherId, projectId: 2));
        yield return Case("another member who is no ProjectAdmin", Admin(TargetId), changes: false, Member(OtherId, ProjectRole.Editor));
        yield return Case("another ProjectAdmin capped at Viewer", Admin(TargetId), changes: false, Admin(OtherId, roleLevel: 1));
        yield return Case("another ProjectAdmin without a global role", Admin(TargetId), changes: false, Admin(OtherId, roleLevel: null));
        yield return Case("another effective ProjectAdmin", Admin(TargetId), changes: true, Admin(OtherId, roleLevel: 2));
        yield return Case("another ProjectAdmin who is a super admin", Admin(TargetId), changes: true, Admin(OtherId, roleLevel: null, superAdmin: true));
        yield return Case("a member who is no ProjectAdmin, alone", Member(TargetId, ProjectRole.Editor), changes: true);
    }

    [TestCaseSource(nameof(Outcomes))]
    public void TheCondition_KeepsTheRow_OnlyWhileAnotherEffectiveProjectAdminRemains(ProjectMember target, ProjectMember[] others, bool changes)
    {
        var rows = new List<ProjectMember> { target };
        rows.AddRange(others);
        using var context = new RecordingBeaconContext(
            new Dictionary<Type, object> { [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(rows, []) },
            [],
            new SqlCapture());

        var kept = Changeable(context).Count();

        kept.Should().Be(changes ? 1 : 0);
    }

    private static IQueryable<ProjectMember> Changeable(BeaconContext context)
    {
        return context.ProjectMembers
            .Where(x => x.ProjectId == ProjectId)
            .Where(x => x.UserId == TargetId)
            .WhereChangeKeepsAProjectAdmin(context, ProjectId, TargetId, mayLeaveNoAdmin: false);
    }

    private static TestCaseData Case(string name, ProjectMember target, bool changes, params ProjectMember[] others)
    {
        return new TestCaseData(target, others, changes).SetName($"LastProjectAdminCondition_With {name}");
    }

    private static ProjectMember Admin(int userId, bool enabled = true, bool archived = false, int projectId = ProjectId, int? roleLevel = 2, bool superAdmin = false)
    {
        return Member(userId, ProjectRole.ProjectAdmin, enabled, archived, projectId, roleLevel, superAdmin);
    }

    private static ProjectMember Member(
        int userId,
        ProjectRole role,
        bool enabled = true,
        bool archived = false,
        int projectId = ProjectId,
        int? roleLevel = 2,
        bool superAdmin = false)
    {
        var user = new BeaconUser
        {
            Id = userId,
            ExternalId = $"ext-{userId}",
            UserName = $"user{userId}",
            IsEnabled = enabled,
            IsSuperAdmin = superAdmin,
            UserRoles = roleLevel == null
                ? []
                : [new BeaconUserRole { UserId = userId, Role = new BeaconRole { Name = "role", Level = roleLevel.Value } }]
        };
        if (archived)
        {
            user.Archive();
        }

        return new ProjectMember { ProjectId = projectId, UserId = userId, Role = role, User = user };
    }
}
