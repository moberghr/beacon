using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Notifications;

/// <summary>
/// Who may read a subscription run (<see cref="QueryExecutionHistory"/>): its metadata, its failure reason, and the
/// result rows stored when the subscription has <c>StoreResults</c> on. Every reader of stored runs (the notifications
/// endpoints, the Home activity feed, the Control Tower detail, a task's executions and result history, a
/// subscription's anomaly chart) applies <see cref="WhereReadableWithin"/> with the scope from <see cref="ScopeOf"/>.
/// </summary>
internal static class StoredRunAccess
{
    /// <summary>
    /// The runs <paramref name="caller"/> may read. No principal, or an anonymous one: none. A caller with an
    /// <c>allowed_projects</c> claim (an API key or a mapped MCP caller restricted to projects): the runs within those
    /// projects, none for a blank or malformed claim. A scoped caller without the claim: none, as it is denied every
    /// project (<c>ProjectAccess</c>). Any other authenticated caller reads every run: interactive users carry no
    /// project restriction yet and are admitted by the permission filter's read check. Project membership for
    /// interactive users belongs here, as the projects of the signed-in user.
    /// </summary>
    public static StoredRunScope ScopeOf(ClaimsPrincipal? caller)
    {
        if (caller?.Identity?.IsAuthenticated != true)
        {
            return StoredRunScope.None;
        }

        var allowedProjectIds = ProjectRestriction.Of(caller);
        if (allowedProjectIds != null)
        {
            return StoredRunScope.WithinProjects(allowedProjectIds);
        }

        return BeaconScopes.IsScopedCaller(caller) ? StoredRunScope.None : StoredRunScope.Unrestricted;
    }

    /// <summary>
    /// Keeps the runs readable within <paramref name="scope"/>: under a project restriction, a run whose recorded data
    /// sources are all held by at least one of the allowed projects, by their current project membership (the rule
    /// saved-query tools follow). A run without recorded data sources (recorded before Beacon kept them) is never
    /// readable under a restriction.
    /// </summary>
    public static IQueryable<QueryExecutionHistory> WhereReadableWithin(
        this IQueryable<QueryExecutionHistory> runs,
        BeaconContext context,
        StoredRunScope scope)
    {
        if (scope.AllowedProjectIds == null)
        {
            return runs;
        }

        var allowed = scope.AllowedProjectIds.ToList();

        return runs
            .Where(x => x.DataSourceIds != null)
            .Where(x => x.DataSourceIds!.Length > 0)
            .Where(x => allowed
                .Any(y => x.DataSourceIds!
                    .All(z => context.ProjectDataSources
                        .Where(w => w.ProjectId == y)
                        .Any(w => w.DataSourceId == z))));
    }
}
