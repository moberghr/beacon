using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class RestoreQueryVersionHandler(
    IQueryVersionService versionService,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ProjectRequestAuthorizer authorizer)
    : IRequestHandler<RestoreQueryVersionCommand, RestoreQueryVersionResult>
{
    public async Task<RestoreQueryVersionResult> Handle(RestoreQueryVersionCommand request, CancellationToken cancellationToken)
    {
        await EnsureMayOverrideLockAsync(request.VersionId, cancellationToken);

        // The restoring user is the request's caller, by the id Beacon records people by (Users.ExternalId).
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var change = await versionService.RestoreVersionAsync(request.VersionId, actor.UserId, cancellationToken);

        return new RestoreQueryVersionResult(
            change.Version.VersionNumber,
            change.Version.Status == QueryVersionStatus.PendingApproval,
            change.SupersededRequests);
    }

    // A locked query is restored by its lock holder, who may wherever it may write, or by a caller who may override another
    // user's lock (Manage); an unlocked one needs Write, which the request checks.
    private async Task EnsureMayOverrideLockAsync(int versionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var target = await context.QueryVersions
            .Where(x => x.Id == versionId)
            .Select(x =>
                new
                {
                    x.QueryId,
                    x.Query.IsLocked
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (target?.IsLocked == true)
        {
            await authorizer.DemandAsync(new ProjectCheck(ProjectResource.Query(target.QueryId), ProjectAction.Manage), cancellationToken);
        }
    }
}

/// <summary>
/// Restores a version that was live. With the approval workflow on the restore is submitted for approval ("Restore of
/// version N") instead of going live. A locked query is restored by its lock holder or a caller who may manage it.
/// </summary>
public record RestoreQueryVersionCommand : IRequest<RestoreQueryVersionResult>, IProjectAuthorizedRequest
{
    public required int VersionId { get; init; }

    [ProjectScopeExempt("ignored: the restoring user is the request's caller")]
    [Obsolete("Ignored: the restoring user is the request's caller.")]
    public string? UserId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.QueryVersion(VersionId), ProjectAction.Write)
    ];
}

/// <param name="VersionNumber">The new version: Active, or PendingApproval when <paramref name="SubmittedForApproval"/>.</param>
/// <param name="SubmittedForApproval">The approval workflow is on: the restore waits for approval and nothing went live.</param>
/// <param name="SupersededRequests">How many pending requests of the query the submitted restore replaced (their
/// requesters are notified); 0 when it went live.</param>
public record RestoreQueryVersionResult(int VersionNumber, bool SubmittedForApproval, int SupersededRequests);
