using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.Projects.Members;

internal sealed class SetProjectAccessModeHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IBeaconActorAccessor actorAccessor,
    IOptions<ProjectAuthorizationOptions> options,
    ILogger<SetProjectAccessModeHandler> logger)
    : IRequestHandler<SetProjectAccessModeCommand, SetProjectAccessModeResult>
{
    public async Task<SetProjectAccessModeResult> Handle(SetProjectAccessModeCommand request, CancellationToken cancellationToken)
    {
        // An Admin now (live roles), whatever role claims the session was signed in with.
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (!scope.IsUnrestricted)
        {
            throw AccessRefusal.Of(logger, actor, "project access mode", request.ProjectId, "Only an Admin can restrict or open a project.");
        }

        // Without memberships a restricted project would be reachable by Admins only. Opening one is always possible.
        if (request.RequiresMembership && !membershipSource.IsAvailable)
        {
            throw new BeaconConflictException("Projects cannot be restricted: project membership is not available in this deployment.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects
            .Where(x => x.Id == request.ProjectId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ProjectAccessGuard.NotFound(request.ProjectId);

        if (project.RequiresMembership != request.RequiresMembership)
        {
            project.RequiresMembership = request.RequiresMembership;
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                ProjectAccessEvents.AccessModeChanged,
                "Project access mode set: project {ProjectId}, requires membership {RequiresMembership}, by user {ActorId}",
                request.ProjectId,
                request.RequiresMembership,
                actor.LogId);
        }

        // The other projects that keep reaching this project's data sources, and who manages them.
        var sharedSourceProjects = await SharedSourceProjects.ReadAsync(context, project.Id, options.Value.RequireProjectMembership, cancellationToken);

        return new SetProjectAccessModeResult(project.Id, project.RequiresMembership, sharedSourceProjects);
    }
}

/// <summary>
/// Restricts a project (only its members and Admins reach it and its data sources) or opens it again. Admin only. 409
/// when restricting while project membership is not available (no Beacon user management and no host membership
/// source). The result lists the other live projects that link any of its data sources, with their ProjectAdmins: they
/// keep reaching those sources whatever this project's mode.
/// </summary>
[ProjectScopeExempt("admin-gated: restricting or opening a project is an Admin's", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record SetProjectAccessModeCommand(int ProjectId, bool RequiresMembership) : IRequest<SetProjectAccessModeResult>;

public record SetProjectAccessModeResult(int ProjectId, bool RequiresMembership, List<SharedSourceProjectEntry> SharedSourceProjects);
