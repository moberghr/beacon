using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Decides <see cref="ProjectCheck"/>s against the caller's project access: the checks a request declares (through
/// <see cref="ProjectAuthorizationBehavior{TRequest, TResponse}"/>) and the ones a handler makes on ids it saves.
/// A resource the caller may not read, or a missing one, is 404 (<see cref="BeaconNotFoundException"/>); an action it may
/// not take on a readable one is 403 (<see cref="BeaconForbiddenException"/>); both are logged with ids only. An
/// unrestricted caller (an Admin, system work in a marked scope) is
/// not checked, and its handler answers a missing resource as before; so is a caller whose access reaches every project
/// for the actions concerned, without a database read. A check a functional permission grants
/// (<see cref="ProjectCheck.GrantedBy"/>) passes for a holder of it once the resource exists; a holder of
/// <see cref="BeaconPermissions.DataSourceAdmin"/> changes shared data-source state without
/// <see cref="ProjectCheck.EveryLinkingProjectAction"/> in every project linking the source (<see cref="LinkingProjects"/>).
/// Permissions are read only when a check could need them.
/// </summary>
internal sealed class ProjectRequestAuthorizer(
    IProjectAccessEvaluator evaluator,
    IProjectResourceResolver resolver,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ILogger<ProjectRequestAuthorizer> logger)
{
    public async Task DemandAsync(IEnumerable<ProjectCheck> checks, CancellationToken cancellationToken)
    {
        var pending = checks.ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var scope = await evaluator.GetScopeAsync(cancellationToken);
        if (scope.IsUnrestricted)
        {
            return;
        }

        var lookups = pending
            .Where(x => !IsDecidedWithoutLookup(scope, x))
            .ToList();
        if (lookups.Count == 0)
        {
            return;
        }

        var granted = await GrantedByPermissionAsync(lookups, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var check in lookups)
        {
            if (granted.Contains(check))
            {
                await DemandExistsAsync(context, check, cancellationToken);
                continue;
            }

            await DemandAsync(context, scope, check, cancellationToken);
        }
    }

    public Task DemandAsync(ProjectCheck check, CancellationToken cancellationToken)
    {
        return DemandAsync([check], cancellationToken);
    }

    /// <summary>
    /// Refuses (409, <see cref="BeaconConflictException"/> with <paramref name="refusal"/>) making
    /// <paramref name="ownerUserId"/> (a <c>Users.ExternalId</c>) the owner of scheduled work unless that user, as they
    /// are now and not the caller, may read the check's resource and take its action: the work runs as its owner and
    /// would otherwise be skipped at every run. The caller's own access is checked by the request.
    /// </summary>
    public async Task DemandOwnerMayAsync(string ownerUserId, ProjectCheck check, string refusal, CancellationToken cancellationToken)
    {
        var owner = await actorAccessor.ResolveOwnerAsync(ownerUserId, cancellationToken);
        var scope = await evaluator.GetScopeAsync(owner, cancellationToken);
        if (scope.IsUnrestricted || IsDecidedWithoutLookup(scope, check))
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var resolved = await resolver.ResolveAsync(context, check.Resource, cancellationToken);
        var holders = new Dictionary<IReadOnlyList<int>, IReadOnlyList<int>>(ReferenceEqualityComparer.Instance);
        var allowed = !resolved.IsMissing
            && await AllowsAsync(context, resolved, scope.For(ProjectAction.Read), holders, cancellationToken)
            && await AllowsAsync(context, resolved, scope.For(check.Action), holders, cancellationToken);
        if (allowed)
        {
            return;
        }

        logger.LogInformation(
            "Owner change refused: {Resource} {ResourceId}, the new owner {OwnerId} may not {Action} it",
            check.Resource.Kind,
            check.Resource.Id,
            owner.LogId,
            check.Action);

        throw new BeaconConflictException(refusal);
    }

    // Allowed everywhere for the actions concerned: nothing to read. A signal recorded without a project is an Admin's
    // alone, so it is always read.
    private static bool IsDecidedWithoutLookup(ProjectAccessScope scope, ProjectCheck check)
    {
        return check.Resource.Kind != ProjectResourceKind.QuerySignal
            && scope.For(ProjectAction.Read).Ids == null
            && scope.For(check.Action).Ids == null
            && (!check.InEveryLinkingProject || scope.For(check.EveryLinkingProjectAction).Ids == null);
    }

    // The checks the caller passes through a functional permission it holds. No read without such a check.
    private async Task<IReadOnlySet<ProjectCheck>> GrantedByPermissionAsync(IReadOnlyList<ProjectCheck> checks, CancellationToken cancellationToken)
    {
        if (checks.All(x => x.GrantedBy == null))
        {
            return new HashSet<ProjectCheck>();
        }

        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);

        return checks
            .Where(x => x.GrantedBy != null)
            .Where(x => permissions.Holds(x.GrantedBy!))
            .ToHashSet();
    }

    private async Task DemandExistsAsync(BeaconContext context, ProjectCheck check, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(context, check.Resource, cancellationToken);
        if (resolved.IsMissing)
        {
            throw await NotFoundAsync(check, AccessRefusal.Missing, cancellationToken);
        }
    }

    private async Task DemandAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        ProjectCheck check,
        CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(context, check.Resource, cancellationToken);
        if (resolved.IsMissing)
        {
            throw await NotFoundAsync(check, AccessRefusal.Missing, cancellationToken);
        }

        var holders = new Dictionary<IReadOnlyList<int>, IReadOnlyList<int>>(ReferenceEqualityComparer.Instance);
        if (!await AllowsAsync(context, resolved, scope.For(ProjectAction.Read), holders, cancellationToken))
        {
            throw await NotFoundAsync(check, AccessRefusal.NotReadable, cancellationToken);
        }

        if (check.Action != ProjectAction.Read)
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
            var asRecordedOwner = resolved.OwnerUserId != null && actor.Is(resolved.OwnerUserId);
            var allowed = scope.For(check.Action, asRecordedOwner);
            if (!await AllowsAsync(context, resolved, allowed, holders, cancellationToken))
            {
                throw Refused(actor, check);
            }
        }

        if (!check.InEveryLinkingProject)
        {
            return;
        }

        if (resolved.SharedDataSourceIds.Count > 0)
        {
            await DemandInEveryLinkingProjectAsync(context, scope, check, resolved.SharedDataSourceIds, cancellationToken);
        }
        else if (resolved.ProjectId is { } projectId && !scope.Allows(projectId, check.EveryLinkingProjectAction))
        {
            // The change writes no shared state (a patch to the project's own documentation): its own project decides.
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

            throw Refused(actor, check with { Action = check.EveryLinkingProjectAction });
        }
    }

    // Shared data-source state (metadata, relationships, descriptions) is grounding for every project linking the
    // source: changing it needs the check's linking action (Write, or Curate) in each of them, or a DataSourceAdmin.
    private async Task DemandInEveryLinkingProjectAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        ProjectCheck check,
        IReadOnlyList<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        var action = check.EveryLinkingProjectAction;
        if (scope.For(action).Ids == null)
        {
            return;
        }

        // The rule the curation lists apply per row (GroundingCuration): every live project linking each source.
        var linking = await LinkingProjects.OfAsync(context, dataSourceIds, cancellationToken);
        if (dataSourceIds.All(x => LinkingProjects.AllowEvery(scope, linking[x].ToList(), action)))
        {
            return;
        }

        // A DataSourceAdmin administers the source for every project: the check's action where it acts (checked above) is
        // enough.
        if ((await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.DataSourceAdmin))
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var refusal = action == ProjectAction.Write
            ? "Changing a shared data source needs write access in every project that links it."
            : "Curating a shared data source needs the project admin role in every project that links it, or the DataSourceAdmin permission.";

        throw AccessRefusal.Of(
            logger,
            actor,
            $"{check.Resource.Kind} (shared data source)",
            check.Resource.Id,
            refusal);
    }

    private static async Task<bool> AllowsAsync(
        BeaconContext context,
        ResolvedProjectResource resolved,
        ProjectSet set,
        Dictionary<IReadOnlyList<int>, IReadOnlyList<int>> holders,
        CancellationToken cancellationToken)
    {
        // An Admin's resource (a signal recorded without a project) stays an Admin's even where everything is open; an
        // Admin is never checked.
        if (resolved.IsAdminOnly)
        {
            return false;
        }

        if (set.Ids == null)
        {
            return true;
        }

        if (resolved.ProjectId != null)
        {
            return set.Contains(resolved.ProjectId.Value);
        }

        if (resolved.IsLegacyRun && set.IsConfinedToList)
        {
            return false;
        }

        foreach (var sources in resolved.SourceSets)
        {
            // No data source (a query without steps): no project holds it and it is not open; its owner reaches it.
            if (sources.Count == 0)
            {
                if (!set.ReachesStepLessQueryOf(resolved.QueryOwnerUserId))
                {
                    return false;
                }

                continue;
            }

            if (set.IncludesOpen && sources.All(x => !set.NotOpenDataSourceIds.Contains(x)))
            {
                continue;
            }

            if (set.Ids.Count == 0)
            {
                return false;
            }

            if (!holders.TryGetValue(sources, out var holding))
            {
                holding = await ProjectSourceFootprint.HoldersOf(context, sources).ToListAsync(cancellationToken);
                holders[sources] = holding;
            }

            if (!holding.Any(set.Ids.Contains))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<BeaconNotFoundException> NotFoundAsync(ProjectCheck check, string reason, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        AccessRefusal.Hidden(logger, actor, check.Resource.Kind.ToString(), check.Resource.Id, check.Action, reason);

        return new BeaconNotFoundException($"{check.Resource.Kind} {check.Resource.Id} not found.");
    }

    private BeaconForbiddenException Refused(BeaconActor actor, ProjectCheck check)
    {
        return AccessRefusal.Of(
            logger,
            actor,
            $"{check.Resource.Kind} ({check.Action})",
            check.Resource.Id,
            $"{check.Action} is not allowed on this {check.Resource.Kind}.");
    }
}
