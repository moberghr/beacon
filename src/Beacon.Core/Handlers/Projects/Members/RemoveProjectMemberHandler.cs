using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.Projects.Members;

internal sealed class RemoveProjectMemberHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    IOptions<RoleMappingOptions> roleMapping,
    ILogger<RemoveProjectMemberHandler> logger)
    : IRequestHandler<RemoveProjectMemberCommand>
{
    public async Task Handle(RemoveProjectMemberCommand request, CancellationToken cancellationToken)
    {
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

        // The last-ProjectAdmin condition and the removal run in one serializable transaction.
        var removed = await ProjectMembershipRules.InSerializableTransactionAsync(
            context,
            () => context.ProjectMembers
                .Where(x => x.ProjectId == request.ProjectId)
                .Where(x => x.UserId == request.UserId)
                .WhereChangeKeepsAProjectAdmin(context, request.ProjectId, request.UserId, mayLeaveNoAdmin: scope.IsUnrestricted)
                .ExecuteDeleteAsync(cancellationToken),
            cancellationToken);

        if (removed == 0)
        {
            throw await SetProjectMemberHandler.RefusedChangeAsync(context, request.ProjectId, request.UserId, cancellationToken);
        }

        logger.LogInformation(
            ProjectAccessEvents.MemberRemoved,
            "Project member removed: project {ProjectId}, user {MemberUserId}, by user {ActorId}",
            request.ProjectId,
            request.UserId,
            actor.LogId);
    }
}

/// <summary>
/// Removes <c>UserId</c> from the project. For a ProjectAdmin of the project, a UserAdmin (never their own membership, nor
/// that of an Admin, a super admin or a functional role holder) or an Admin. The last effective ProjectAdmin (active, with a global role of Editor or above) cannot be removed (an Admin
/// may). 409 when the host manages membership, and when the members change concurrently (retry).
/// </summary>
public record RemoveProjectMemberCommand(
    int ProjectId,
    [property: ProjectScopeExempt("the member: a user, not a project resource")] int UserId) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.OrPermission(ProjectResource.Project(ProjectId), ProjectAction.ManageMembers, BeaconPermissions.UserAdmin)
    ];
}
