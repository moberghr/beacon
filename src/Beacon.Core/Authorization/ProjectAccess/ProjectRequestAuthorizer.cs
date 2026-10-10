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
/// unrestricted caller (an Admin, work without a request) is
/// not checked, and its handler answers a missing resource as before; so is a caller whose access reaches every project
/// for the actions concerned, without a database read.
/// </summary>
internal sealed class ProjectRequestAuthorizer(
    IProjectAccessEvaluator evaluator,
    IProjectResourceResolver resolver,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
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

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var check in lookups)
        {
            await DemandAsync(context, scope, check, cancellationToken);
        }
    }

    public Task DemandAsync(ProjectCheck check, CancellationToken cancellationToken)
    {
        return DemandAsync([check], cancellationToken);
    }

    // Allowed everywhere for the actions concerned: nothing to read. A signal recorded without a project is an Admin's
    // alone, so it is always read.
    private static bool IsDecidedWithoutLookup(ProjectAccessScope scope, ProjectCheck check)
    {
        return check.Resource.Kind != ProjectResourceKind.QuerySignal
            && scope.For(ProjectAction.Read).Ids == null
            && scope.For(check.Action).Ids == null
            && (!check.InEveryLinkingProject || scope.For(ProjectAction.Write).Ids == null);
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

        if (check.InEveryLinkingProject && resolved.SharedDataSourceIds.Count > 0)
        {
            await DemandWriteInEveryLinkingProjectAsync(context, scope, check, resolved.SharedDataSourceIds, cancellationToken);
        }
    }

    // Shared data-source state (metadata, relationships, descriptions) is grounding for every project linking the
    // source: changing it needs Write in each of them.
    private async Task DemandWriteInEveryLinkingProjectAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        ProjectCheck check,
        IReadOnlyList<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        if (scope.For(ProjectAction.Write).Ids == null)
        {
            return;
        }

        var linking = await context.Projects
            .Where(x => x.DataSources.Any(y => dataSourceIds.Contains(y.DataSourceId)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (linking.All(x => scope.Allows(x, ProjectAction.Write)))
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        throw AccessRefusal.Of(
            logger,
            actor,
            $"{check.Resource.Kind} (shared data source)",
            check.Resource.Id,
            "Changing a shared data source needs write access in every project that links it.");
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
