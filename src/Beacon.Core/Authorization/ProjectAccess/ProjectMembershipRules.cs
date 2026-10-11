using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// What every membership and link route checks first: that Beacon manages project membership (not the host, and it is
/// available), and that the caller may manage the members of a live project (for membership itself, a UserAdmin may too).
/// </summary>
internal static class ProjectMembershipRules
{
    public const string ManagedByHostMessage = "Project membership is managed by the host.";

    public const string UnavailableMessage = "Project membership is not available in this deployment.";

    public const string ConcurrentChangeMessage = "The project's members changed concurrently; reload and retry.";

    /// <summary>
    /// Who manages membership: nobody while the membership source is unavailable (the default source without Beacon
    /// user management, or a host source that reports none), Beacon with the default source, the host with its own.
    /// </summary>
    public static ProjectMembershipManagement ManagementOf(IProjectMembershipSource membershipSource)
    {
        if (!membershipSource.IsAvailable)
        {
            return ProjectMembershipManagement.Unavailable;
        }

        return membershipSource is DatabaseProjectMembershipSource
            ? ProjectMembershipManagement.Beacon
            : ProjectMembershipManagement.Host;
    }

    /// <summary>Beacon manages membership when the default membership source is in use and available (Beacon user management on).</summary>
    public static bool IsManagedByBeacon(IProjectMembershipSource membershipSource)
    {
        return ManagementOf(membershipSource) == ProjectMembershipManagement.Beacon;
    }

    /// <summary>409 when the host manages membership, or when membership is not available.</summary>
    public static void EnsureManagedByBeacon(IProjectMembershipSource membershipSource)
    {
        var management = ManagementOf(membershipSource);
        if (management != ProjectMembershipManagement.Beacon)
        {
            throw new BeaconConflictException(management == ProjectMembershipManagement.Host ? ManagedByHostMessage : UnavailableMessage);
        }
    }

    /// <summary>
    /// The live project <paramref name="projectId"/>, when the caller may manage its members: 404 when it is missing,
    /// archived or unreadable to the caller, 403 when the caller may read it but not manage its members.
    /// </summary>
    public static async Task<LiveProject> RequireManageMembersAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        int projectId,
        BeaconActor actor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var project = await context.Projects
            .Where(x => x.Id == projectId)
            .Select(x => new LiveProject(x.Id, x.HostManagedKey))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ProjectAccessGuard.NotFound(logger, actor, projectId, ProjectAction.ManageMembers, AccessRefusal.Missing);

        ProjectAccessGuard.Demand(scope, projectId, ProjectAction.ManageMembers, actor, logger);

        return project;
    }

    public const string ProtectedMemberMessage =
        "Only an Admin can change the project membership of an Admin, a super admin or a holder of a functional role.";

    /// <summary>
    /// The live project <paramref name="projectId"/>, when the caller may manage its members: a ProjectAdmin of it or an
    /// Admin (<see cref="RequireManageMembersAsync"/>), or a holder of <see cref="BeaconPermissions.UserAdmin"/>, who
    /// manages the members of every project but never their own membership (<paramref name="memberUserId"/>, the member a
    /// change is about; null for a read), nor that of a user whose account only an Admin may change
    /// (<see cref="ProtectedUsers"/>: an Admin, a super admin, a functional role holder, another UserAdmin among them, so
    /// two UserAdmins cannot add each other): 403 when they try. Linking data sources stays a ProjectAdmin's or an Admin's.
    /// </summary>
    public static async Task<LiveProject> RequireMembershipAdministrationAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        BeaconPermissionSet permissions,
        int projectId,
        int? memberUserId,
        BeaconActor actor,
        ILogger logger,
        CancellationToken cancellationToken,
        RoleMappingOptions? roleMapping = null)
    {
        if (scope.Allows(projectId, ProjectAction.ManageMembers) || !permissions.Holds(BeaconPermissions.UserAdmin))
        {
            return await RequireManageMembersAsync(context, scope, projectId, actor, logger, cancellationToken);
        }

        var project = await context.Projects
            .Where(x => x.Id == projectId)
            .Select(x => new LiveProject(x.Id, x.HostManagedKey))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ProjectAccessGuard.NotFound(logger, actor, projectId, ProjectAction.ManageMembers, AccessRefusal.Missing);

        if (memberUserId != null && memberUserId == actor.BeaconUserId)
        {
            throw AccessRefusal.Of(logger, actor, "project membership", projectId, "A UserAdmin cannot change their own project membership.");
        }

        if (memberUserId != null
            && await ProtectedUsers.IsProtectedAsync(context, memberUserId.Value, roleMapping ?? new RoleMappingOptions(), cancellationToken))
        {
            throw AccessRefusal.Of(logger, actor, "project membership", projectId, ProtectedMemberMessage);
        }

        return project;
    }

    /// <summary>
    /// The users among <paramref name="userIds"/> whose membership of project <paramref name="projectId"/> the caller may
    /// not add, change or remove by <see cref="RequireMembershipAdministrationAsync"/>: none for a ProjectAdmin of the
    /// project or an Admin; for a UserAdmin who is neither, themselves and every protected user. So a list can show which
    /// changes would be refused.
    /// </summary>
    public static async Task<IReadOnlySet<int>> UnchangeableMembersAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        BeaconPermissionSet permissions,
        int projectId,
        IReadOnlyCollection<int> userIds,
        BeaconActor actor,
        RoleMappingOptions roleMapping,
        CancellationToken cancellationToken)
    {
        if (scope.Allows(projectId, ProjectAction.ManageMembers) || !permissions.Holds(BeaconPermissions.UserAdmin))
        {
            return new HashSet<int>();
        }

        var unchangeable = (await ProtectedUsers.ProtectedAmongAsync(context, userIds, roleMapping, cancellationToken)).ToHashSet();
        if (actor.BeaconUserId != null && userIds.Contains(actor.BeaconUserId.Value))
        {
            unchangeable.Add(actor.BeaconUserId.Value);
        }

        return unchangeable;
    }

    /// <summary>
    /// Keeps the membership rows a change may touch: any row that is not a ProjectAdmin's, and a ProjectAdmin's only while
    /// another user is an effective ProjectAdmin of the project (enabled, not archived, and with a global role of Editor
    /// or above; a ProjectAdmin capped at Viewer manages nothing), so a project never loses its last ProjectAdmin to a
    /// demotion or removal. The condition is part of the statement that makes the change; callers run it in a
    /// serializable transaction, so two concurrent changes cannot each count the other's ProjectAdmin and both pass. An
    /// Admin (<paramref name="mayLeaveNoAdmin"/>) is exempt.
    /// </summary>
    public static IQueryable<ProjectMember> WhereChangeKeepsAProjectAdmin(
        this IQueryable<ProjectMember> members,
        BeaconContext context,
        int projectId,
        int userId,
        bool mayLeaveNoAdmin)
    {
        if (mayLeaveNoAdmin)
        {
            return members;
        }

        return members
            .Where(x => x.Role != ProjectRole.ProjectAdmin
                || context.ProjectMembers
                    .Where(y => y.ProjectId == projectId)
                    .Where(y => y.UserId != userId)
                    .Where(y => y.Role == ProjectRole.ProjectAdmin)
                    .Where(y => y.User.IsEnabled)
                    .Where(y => y.User.ArchivedTime == null)
                    .Any(y => y.User.IsSuperAdmin || y.User.UserRoles.Any(z => z.Role.Level >= RoleService.RoleLevels.Editor)));
    }

    /// <summary>
    /// Runs a membership change in a serializable transaction (the last-ProjectAdmin condition and the write see the same
    /// rows); a conflict with a concurrent change is a 409 to retry.
    /// </summary>
    public static async Task<T> InSerializableTransactionAsync<T>(
        BeaconContext context,
        Func<Task<T>> change,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var result = await change();
            await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            throw new BeaconConflictException(ConcurrentChangeMessage);
        }
    }
}

/// <summary>A live project, and the key of the host that declared it (null for a project created in Beacon).</summary>
internal sealed record LiveProject(int Id, string? HostManagedKey);
