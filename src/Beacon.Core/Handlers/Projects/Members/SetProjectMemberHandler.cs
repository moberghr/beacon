using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.Projects.Members;

internal sealed class SetProjectMemberHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    IOptions<RoleMappingOptions> roleMapping,
    TimeProvider timeProvider,
    ILogger<SetProjectMemberHandler> logger)
    : IRequestHandler<SetProjectMemberCommand, SetProjectMemberResult>
{
    public async Task<SetProjectMemberResult> Handle(SetProjectMemberCommand request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Role))
        {
            throw new InvalidOperationException("The role must be Viewer, Editor or ProjectAdmin.");
        }

        ProjectMembershipRules.EnsureManagedByBeacon(membershipSource);

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ProjectMembershipRules.RequireMembershipAdministrationAsync(
            context,
            scope,
            await permissionAccessor.GetCurrentAsync(cancellationToken),
            request.ProjectId,
            request.UserId,
            actor,
            logger,
            cancellationToken,
            roleMapping.Value);

        var isActiveUser = await context.Users
            .Where(x => x.Id == request.UserId)
            .Where(x => x.IsEnabled)
            .AnyAsync(cancellationToken);
        if (!isActiveUser)
        {
            throw new BeaconNotFoundException($"User {request.UserId} not found.");
        }

        var hasRow = await context.ProjectMembers
            .Where(x => x.ProjectId == request.ProjectId)
            .Where(x => x.UserId == request.UserId)
            .AnyAsync(cancellationToken);

        // Upsert as an insert first; a concurrent insert of the same member makes it an update.
        if (!hasRow && await TryAddAsync(context, request, actor, cancellationToken))
        {
            logger.LogInformation(
                ProjectAccessEvents.MemberAdded,
                "Project member added: project {ProjectId}, user {MemberUserId}, role {Role}, by user {ActorId}",
                request.ProjectId,
                request.UserId,
                request.Role,
                actor.LogId);

            return new SetProjectMemberResult(request.ProjectId, request.UserId, request.Role, true);
        }

        // The last-ProjectAdmin condition and the role change run in one serializable transaction.
        var updated = await ProjectMembershipRules.InSerializableTransactionAsync(
            context,
            () => context.ProjectMembers
                .Where(x => x.ProjectId == request.ProjectId)
                .Where(x => x.UserId == request.UserId)
                .WhereChangeKeepsAProjectAdmin(
                    context,
                    request.ProjectId,
                    request.UserId,
                    mayLeaveNoAdmin: request.Role == ProjectRole.ProjectAdmin || scope.IsUnrestricted)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.Role, request.Role)
                        .SetProperty(y => y.ModifiedTime, timeProvider.GetUtcNow().UtcDateTime),
                    cancellationToken),
            cancellationToken);

        if (updated == 0)
        {
            throw await RefusedChangeAsync(context, request.ProjectId, request.UserId, cancellationToken);
        }

        logger.LogInformation(
            ProjectAccessEvents.MemberRoleChanged,
            "Project member role set: project {ProjectId}, user {MemberUserId}, role {Role}, by user {ActorId}",
            request.ProjectId,
            request.UserId,
            request.Role,
            actor.LogId);

        return new SetProjectMemberResult(request.ProjectId, request.UserId, request.Role, false);
    }

    private static async Task<bool> TryAddAsync(
        BeaconContext context,
        SetProjectMemberCommand request,
        BeaconActor actor,
        CancellationToken cancellationToken)
    {
        context.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = request.ProjectId,
            UserId = request.UserId,
            Role = request.Role,
            AddedByUserId = actor.BeaconUserId
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            return false;
        }
    }

    // Nothing changed: the row is a ProjectAdmin's and no other effective ProjectAdmin is left, or the row went away
    // concurrently.
    internal static async Task<Exception> RefusedChangeAsync(
        BeaconContext context,
        int projectId,
        int userId,
        CancellationToken cancellationToken)
    {
        var stillMember = await context.ProjectMembers
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.UserId == userId)
            .AnyAsync(cancellationToken);

        return stillMember
            ? new InvalidOperationException("A project keeps at least one active project admin; add another before changing this one.")
            : new BeaconNotFoundException($"User {userId} is not a member of project {projectId}.");
    }
}

/// <summary>
/// Adds <c>UserId</c> to the project with <c>Role</c>, or changes their role. For a ProjectAdmin of the project, a
/// UserAdmin (never their own membership, nor that of an Admin, a super admin or a functional role holder) or an Admin. The last effective ProjectAdmin (active, with a global role of
/// Editor or above) cannot be demoted (an Admin may). 409 when the host manages membership, and when the members change
/// concurrently (retry).
/// </summary>
public record SetProjectMemberCommand(
    int ProjectId,
    [property: ProjectScopeExempt("the member: a user, not a project resource")] int UserId,
    ProjectRole Role) : IRequest<SetProjectMemberResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.OrPermission(ProjectResource.Project(ProjectId), ProjectAction.ManageMembers, BeaconPermissions.UserAdmin)
    ];
}

public record SetProjectMemberResult(int ProjectId, int UserId, ProjectRole Role, bool Added);
