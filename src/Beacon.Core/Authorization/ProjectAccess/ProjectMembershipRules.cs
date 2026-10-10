using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// What every membership and link route checks first: that Beacon manages project membership (not the host, and it is
/// available), and that the caller may manage the members of a live project.
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
