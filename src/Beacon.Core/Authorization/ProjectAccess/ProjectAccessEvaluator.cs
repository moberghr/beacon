using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Mcp;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The default <see cref="IProjectAccessEvaluator"/>. Classifies the caller, reads one <see cref="ProjectAccessSnapshot"/>
/// (memoized per scope, never across requests) and grants per project: the lower of the membership role and the
/// caller's global role, raised to the open grant in an open project; then confines a caller that carries a project
/// list to it and caps the actions by the caller's scope. When no project is restricted or archived, every data source
/// is openly reachable and the caller carries no list, each action within the caller's global role is allowed
/// everywhere (<see cref="ProjectSet.All"/>), so filters add nothing to the SQL; managing members is still decided from
/// the membership rows. Elsewhere a query without steps (no project holds it) is reached by its owner alone, for Read and
/// Write, unless the caller is confined to a project list (<see cref="ProjectSet.QueryOwnerUserId"/>).
/// </summary>
internal sealed class ProjectAccessEvaluator(
    IBeaconActorAccessor actorAccessor,
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectMembershipSource membershipSource,
    IRunAsCeilingSource ceilingSource,
    IServiceProvider services,
    BeaconConfiguration configuration,
    IOptions<ProjectAuthorizationOptions> options,
    ILogger<ProjectAccessEvaluator> logger,
    OwnerRunsAsSystemLog ownerRunsAsSystemLog) : IProjectAccessEvaluator
{
    private static readonly IReadOnlySet<ProjectAction> AllActions = Enum.GetValues<ProjectAction>().ToHashSet();

    private static readonly IReadOnlySet<ProjectAction> NonInteractiveActions = AllActions
        .Where(x => x != ProjectAction.ManageMembers)
        .ToHashSet();

    // What a configured MCP project grants without a Beacon user behind it.
    private static readonly IReadOnlySet<ProjectAction> ConfiguredGrantActions = new HashSet<ProjectAction>
    {
        ProjectAction.Read,
        ProjectAction.Run,
        ProjectAction.Query
    };

    private static readonly IReadOnlySet<ProjectAction> ReadScopeActions = new HashSet<ProjectAction> { ProjectAction.Read };

    // What the owner of a query without steps may do with it (and with the work derived from it).
    private static readonly IReadOnlySet<ProjectAction> StepLessQueryOwnerActions = new HashSet<ProjectAction>
    {
        ProjectAction.Read,
        ProjectAction.Write
    };

    private readonly Dictionary<BeaconActor, ProjectAccessScope> _explicitScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, ProjectAccessSnapshot> _snapshots = new();
    private ProjectAccessScope? _current;

    private bool RequiresMembershipEverywhere => options.Value.RequireProjectMembership;

    public async Task<ProjectAccessScope> GetScopeAsync(CancellationToken cancellationToken)
    {
        if (_current != null)
        {
            return _current;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        _current = await EvaluateAsync(actor, isCurrentRequest: true, cancellationToken);

        return _current;
    }

    public async Task<ProjectAccessScope> GetScopeAsync(BeaconActor actor, CancellationToken cancellationToken)
    {
        if (_explicitScopes.TryGetValue(actor, out var scope))
        {
            return scope;
        }

        scope = await EvaluateAsync(actor, isCurrentRequest: false, cancellationToken);
        _explicitScopes[actor] = scope;

        return scope;
    }

    private async Task<ProjectAccessScope> EvaluateAsync(BeaconActor actor, bool isCurrentRequest, CancellationToken cancellationToken)
    {
        var profile = await ProfileAsync(actor, isCurrentRequest, cancellationToken);
        if (profile.IsUnrestricted)
        {
            return ProjectAccessScope.Unrestricted;
        }

        if (profile.IsNone)
        {
            return ProjectAccessScope.None;
        }

        var readsSnapshotRoles = profile.UsesMemberships
            && membershipSource is DatabaseProjectMembershipSource
            && membershipSource.IsAvailable;
        var snapshot = await SnapshotAsync(readsSnapshotRoles ? actor.BeaconUserId : null, cancellationToken);
        var memberships = await MembershipsAsync(actor, profile, snapshot, readsSnapshotRoles, cancellationToken);

        var opensProjects = profile.OpenGrant != null && !RequiresMembershipEverywhere;
        var grants = new Dictionary<int, ProjectGrant>();
        foreach (var project in snapshot.Projects)
        {
            var grant = GrantIn(project, profile, memberships, opensProjects);
            if (grant != null)
            {
                grants[project.Id] = grant;
            }
        }

        var includesOpenFor = profile.ReachesOpenSources && opensProjects
            ? ActionsOf(profile.OpenGrant!.Value, isOpenProject: true, profile.Actions)
            : new HashSet<ProjectAction>();
        var isFastPath = !RequiresMembershipEverywhere
            && snapshot.IsAllOpen
            && profile.ProjectList == null
            && profile.ReachesOpenSources;
        var isConfinedToList = profile.ProjectList != null && !profile.ReachesOpenSources;

        var sets = new Dictionary<ProjectAction, ProjectSet>();
        foreach (var action in AllActions)
        {
            var includesOpen = includesOpenFor.Contains(action) && action != ProjectAction.ManageMembers;
            if (isFastPath && includesOpen)
            {
                sets[action] = ProjectSet.All;
                continue;
            }

            var ids = grants
                .Where(x => x.Value.Actions.Contains(action))
                .Select(x => x.Key)
                .Order()
                .ToList();

            // A query without steps is no project's: its owner reaches it (Read, Write) wherever it may take the action
            // at all, unless it is confined to a project list.
            var reachesOwnStepLessQueries = !isConfinedToList
                && actor.UserId != null
                && StepLessQueryOwnerActions.Contains(action)
                && (ids.Count > 0 || includesOpen);
            sets[action] = new ProjectSet(ids, includesOpen, snapshot.NotOpenDataSourceIds)
            {
                IsConfinedToList = isConfinedToList,
                QueryOwnerUserId = reachesOwnStepLessQueries ? actor.UserId : null
            };
        }

        return new ProjectAccessScope(false, sets, grants);
    }

    private static ProjectGrant? GrantIn(
        ProjectAccessRow project,
        AccessProfile profile,
        IReadOnlyDictionary<int, ProjectRole> memberships,
        bool opensProjects)
    {
        // An archived project grants nothing, on any path.
        if (project.IsArchived)
        {
            return null;
        }

        if (profile.ProjectList != null && !profile.ProjectList.Contains(project.Id))
        {
            return null;
        }

        // An operator-configured project grants its fixed actions, restricted or not.
        if (profile.ListGrantsDirectly)
        {
            return new ProjectGrant(ProjectRole.Viewer, profile.Actions);
        }

        var isOpenProject = opensProjects && !project.RequiresMembership;
        var memberRole = memberships.TryGetValue(project.Id, out var role)
            ? Min(role, profile.Ceiling == ProjectRole.Viewer ? ProjectRole.Viewer : ProjectRole.ProjectAdmin)
            : (ProjectRole?)null;
        var openRole = isOpenProject ? profile.OpenGrant : null;
        var effective = Max(memberRole, openRole);
        if (effective == null)
        {
            return null;
        }

        var actions = ActionsOf(effective.Value, isOpenProject, profile.Actions);
        if (memberRole != ProjectRole.ProjectAdmin)
        {
            // Managing members is never an open grant: only a ProjectAdmin membership gives it.
            actions.Remove(ProjectAction.ManageMembers);
        }

        return actions.Count == 0 ? null : new ProjectGrant(effective.Value, actions);
    }

    // Viewer reads and runs; Editor also queries and writes, and manages in an open project; ProjectAdmin manages
    // and manages members.
    private static HashSet<ProjectAction> ActionsOf(ProjectRole role, bool isOpenProject, IReadOnlySet<ProjectAction> cap)
    {
        var actions = new HashSet<ProjectAction> { ProjectAction.Read, ProjectAction.Run };
        if (role >= ProjectRole.Editor)
        {
            actions.Add(ProjectAction.Query);
            actions.Add(ProjectAction.Write);
        }

        if (role == ProjectRole.ProjectAdmin || (isOpenProject && role >= ProjectRole.Editor))
        {
            actions.Add(ProjectAction.Manage);
        }

        if (role == ProjectRole.ProjectAdmin)
        {
            actions.Add(ProjectAction.ManageMembers);
        }

        actions.IntersectWith(cap);

        return actions;
    }

    private async Task<AccessProfile> ProfileAsync(BeaconActor actor, bool isCurrentRequest, CancellationToken cancellationToken)
    {
        switch (actor.Kind)
        {
            case BeaconPrincipalKind.System:
                // Work without a request in a scope marked as system work (jobs, startup) is unrestricted; work done for a
                // user resolves its owner instead.
                return AccessProfile.Unrestricted;

            case BeaconPrincipalKind.User when actor.IsActiveUser && actor.BeaconUserId != null:
                if (actor.IsLiveAdmin)
                {
                    return AccessProfile.Unrestricted;
                }

                return Interactive(await CeilingAsync(actor, isCurrentRequest, cancellationToken), usesMemberships: true);

            case BeaconPrincipalKind.Session:
                // No stored user. With Beacon user management on, every caller has one, so this one reaches nothing.
                // Without it (a host principal, an unbound bearer), the Admin role claim is honoured and memberships come
                // only from a host membership source.
                if (configuration.UserManagement.Enabled)
                {
                    return AccessProfile.None;
                }

                if (actor.IsAdmin)
                {
                    return AccessProfile.Unrestricted;
                }

                return Interactive(
                    await CeilingAsync(actor, isCurrentRequest, cancellationToken),
                    usesMemberships: membershipSource is not DatabaseProjectMembershipSource);

            case BeaconPrincipalKind.ApiKey:
                return await ApiKeyAsync(actor, isCurrentRequest, cancellationToken);

            case BeaconPrincipalKind.McpCaller:
                return await McpCallerAsync(actor, cancellationToken);

            case BeaconPrincipalKind.Owner:
                return await OwnerAsync(actor, cancellationToken);

            default:
                return AccessProfile.None;
        }
    }

    private static AccessProfile Interactive(ProjectRole? ceiling, bool usesMemberships)
    {
        return ceiling == null
            ? AccessProfile.None
            : new AccessProfile
            {
                Ceiling = ceiling.Value,
                OpenGrant = OpenGrantOf(ceiling.Value),
                ReachesOpenSources = true,
                UsesMemberships = usesMemberships,
                Actions = AllActions
            };
    }

    // An API key acts as its owner, capped by the lower of its scope and its owner's global role (a key never exceeds
    // its owner; an owner who may neither read nor write gives it nothing), never with the Admin bypass. A key without a
    // project list acts in its owner's projects; one with a list only in those of them.
    private async Task<AccessProfile> ApiKeyAsync(BeaconActor actor, bool isCurrentRequest, CancellationToken cancellationToken)
    {
        if (actor.BeaconUserId == null || !actor.IsActiveUser || actor.ScopeCeiling == null)
        {
            return AccessProfile.None;
        }

        var ownerCeiling = await CeilingAsync(actor, isCurrentRequest, cancellationToken);
        if (ownerCeiling == null)
        {
            return AccessProfile.None;
        }

        var ceiling = Min(CeilingOf(actor.ScopeCeiling.Value), ownerCeiling.Value);

        return new AccessProfile
        {
            Ceiling = ceiling,
            OpenGrant = OpenGrantOf(ceiling),
            ReachesOpenSources = actor.AllowedProjectIds == null,
            ProjectList = actor.AllowedProjectIds,
            UsesMemberships = true,
            Actions = ScopeActions(actor.ScopeCeiling.Value)
        };
    }

    // A mapped MCP caller: a system caller gets its configured projects; a user caller its configured projects within its
    // user's access (a user caller without a provisioned user: within the open projects). A caller without a scope was
    // not mapped and gets nothing.
    private async Task<AccessProfile> McpCallerAsync(BeaconActor actor, CancellationToken cancellationToken)
    {
        if (actor.ScopeCeiling == null || actor.CallerKind == null)
        {
            return AccessProfile.None;
        }

        var configured = actor.AllowedProjectIds ?? [];
        var scopeActions = ScopeActions(actor.ScopeCeiling.Value);
        var scopeCeiling = CeilingOf(actor.ScopeCeiling.Value);
        if (actor.CallerKind == McpCallerKind.System)
        {
            return new AccessProfile
            {
                Ceiling = scopeCeiling,
                ProjectList = configured,
                ListGrantsDirectly = true,
                Actions = Intersect(ConfiguredGrantActions, scopeActions)
            };
        }

        if (actor.BeaconUserId == null)
        {
            return new AccessProfile
            {
                Ceiling = scopeCeiling,
                OpenGrant = OpenGrantOf(scopeCeiling),
                ProjectList = configured,
                Actions = Intersect(ConfiguredGrantActions, scopeActions)
            };
        }

        var userCeiling = await ceilingSource.GetCeilingAsync(actor, cancellationToken);
        if (userCeiling == null)
        {
            return AccessProfile.None;
        }

        var ceiling = Min(userCeiling.Value, scopeCeiling);

        return new AccessProfile
        {
            Ceiling = ceiling,
            OpenGrant = OpenGrantOf(ceiling),
            ProjectList = configured,
            UsesMemberships = true,
            Actions = scopeActions
        };
    }

    // The owner of scheduled work: an ownerless row reaches only the openly reachable data sources; an owner without a
    // stored user is the system on a host without user management (logged, ids only) and nobody otherwise.
    private async Task<AccessProfile> OwnerAsync(BeaconActor actor, CancellationToken cancellationToken)
    {
        if (actor.UserId == null)
        {
            // The empty project list keeps every project out; the openly reachable sources still count.
            return new AccessProfile
            {
                Ceiling = ProjectRole.Editor,
                OpenGrant = ProjectRole.Editor,
                ReachesOpenSources = true,
                ProjectList = [],
                Actions = NonInteractiveActions
            };
        }

        if (actor.BeaconUserId == null)
        {
            if (configuration.UserManagement.Enabled)
            {
                return AccessProfile.None;
            }

            ownerRunsAsSystemLog.Report(actor.UserId);

            return AccessProfile.Unrestricted;
        }

        if (!actor.IsActiveUser)
        {
            return AccessProfile.None;
        }

        if (actor.IsLiveAdmin)
        {
            return AccessProfile.Unrestricted;
        }

        var ceiling = await ceilingSource.GetCeilingAsync(actor, cancellationToken);
        if (ceiling == null)
        {
            return AccessProfile.None;
        }

        return new AccessProfile
        {
            Ceiling = ceiling.Value,
            OpenGrant = OpenGrantOf(ceiling.Value),
            ReachesOpenSources = true,
            UsesMemberships = true,
            Actions = NonInteractiveActions
        };
    }

    // The request's own caller is capped by the authorization provider, as the permission filter is: write → Editor,
    // read → Viewer. With enforcement off every signed-in caller writes, as before. Any other actor is capped by the
    // ceiling source.
    private async Task<ProjectRole?> CeilingAsync(BeaconActor actor, bool isCurrentRequest, CancellationToken cancellationToken)
    {
        if (!isCurrentRequest)
        {
            return await ceilingSource.GetCeilingAsync(actor, cancellationToken);
        }

        if (!configuration.Authorization.Enabled && !configuration.UserManagement.Enabled)
        {
            return ProjectRole.Editor;
        }

        var provider = services.GetRequiredService<IBeaconAuthorizationProvider>();
        if (await provider.HasWritePermissionAsync(cancellationToken))
        {
            return ProjectRole.Editor;
        }

        return await provider.HasReadPermissionAsync(cancellationToken) ? ProjectRole.Viewer : null;
    }

    private async Task<ProjectAccessSnapshot> SnapshotAsync(int? memberUserId, CancellationToken cancellationToken)
    {
        var key = memberUserId ?? 0;
        if (_snapshots.TryGetValue(key, out var snapshot))
        {
            return snapshot;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        snapshot = await ProjectAccessSnapshot.ReadAsync(context, memberUserId, cancellationToken);
        _snapshots[key] = snapshot;

        return snapshot;
    }

    // Memberships from the snapshot (the default source), or from a host source. An unavailable source, a null answer or
    // a failure grants no membership: restricted projects are denied for the request, never opened.
    private async Task<IReadOnlyDictionary<int, ProjectRole>> MembershipsAsync(
        BeaconActor actor,
        AccessProfile profile,
        ProjectAccessSnapshot snapshot,
        bool readsSnapshotRoles,
        CancellationToken cancellationToken)
    {
        if (!profile.UsesMemberships || !membershipSource.IsAvailable)
        {
            return new Dictionary<int, ProjectRole>();
        }

        if (readsSnapshotRoles)
        {
            return snapshot.Projects
                .Where(x => x.MemberRole != null)
                .ToDictionary(x => x.Id, x => x.MemberRole!.Value);
        }

        if (membershipSource is DatabaseProjectMembershipSource)
        {
            return new Dictionary<int, ProjectRole>();
        }

        try
        {
            var memberships = await membershipSource.GetMembershipsAsync(actor, cancellationToken);
            if (memberships == null)
            {
                logger.LogWarning("The project membership source returned no memberships; restricted projects are denied for user {UserId}", actor.BeaconUserId);

                return new Dictionary<int, ProjectRole>();
            }

            return KnownRolesOf(memberships, actor);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The project membership source failed; restricted projects are denied for user {UserId}", actor.BeaconUserId);

            return new Dictionary<int, ProjectRole>();
        }
    }

    // A membership whose role is no ProjectRole grants nothing: it is dropped rather than read as some role.
    private IReadOnlyDictionary<int, ProjectRole> KnownRolesOf(IReadOnlyDictionary<int, ProjectRole> memberships, BeaconActor actor)
    {
        var known = memberships
            .Where(x => Enum.IsDefined(x.Value))
            .ToDictionary(x => x.Key, x => x.Value);
        if (known.Count < memberships.Count)
        {
            logger.LogWarning(
                "The project membership source returned {Count} memberships with an unknown role for user {UserId}; they grant nothing",
                memberships.Count - known.Count,
                actor.BeaconUserId);
        }

        return known;
    }

    private static ProjectRole CeilingOf(McpCallerScope scope)
    {
        return scope == McpCallerScope.Execute ? ProjectRole.Editor : ProjectRole.Viewer;
    }

    // A Read scope keeps Read only; scoped callers never manage members.
    private static IReadOnlySet<ProjectAction> ScopeActions(McpCallerScope scope)
    {
        return scope == McpCallerScope.Execute ? NonInteractiveActions : ReadScopeActions;
    }

    private static ProjectRole OpenGrantOf(ProjectRole ceiling)
    {
        return ceiling == ProjectRole.Viewer ? ProjectRole.Viewer : ProjectRole.Editor;
    }

    private static IReadOnlySet<ProjectAction> Intersect(IReadOnlySet<ProjectAction> first, IReadOnlySet<ProjectAction> second)
    {
        return first
            .Where(second.Contains)
            .ToHashSet();
    }

    private static ProjectRole Min(ProjectRole first, ProjectRole second)
    {
        return first <= second ? first : second;
    }

    private static ProjectRole? Max(ProjectRole? first, ProjectRole? second)
    {
        if (first == null)
        {
            return second;
        }

        return second == null || first >= second ? first : second;
    }

    /// <summary>How a classified caller is granted, before the snapshot is read.</summary>
    private sealed class AccessProfile
    {
        public static AccessProfile Unrestricted { get; } = new() { IsUnrestricted = true };

        public static AccessProfile None { get; } = new() { IsNone = true };

        public bool IsUnrestricted { get; init; }

        public bool IsNone { get; init; }

        /// <summary>The global role that caps membership roles: Viewer, or Editor (a writer may be a ProjectAdmin).</summary>
        public ProjectRole Ceiling { get; init; } = ProjectRole.Viewer;

        /// <summary>The role an open project grants; null when open projects grant nothing to this caller.</summary>
        public ProjectRole? OpenGrant { get; init; }

        /// <summary>
        /// Whether the openly reachable data sources count: never for a caller with a project list, except ownerless
        /// scheduled work, which reaches nothing else.
        /// </summary>
        public bool ReachesOpenSources { get; init; }

        public IReadOnlyList<int>? ProjectList { get; init; }

        /// <summary>The project list grants <see cref="Actions"/> by itself (an operator-configured MCP system caller).</summary>
        public bool ListGrantsDirectly { get; init; }

        public bool UsesMemberships { get; init; }

        public IReadOnlySet<ProjectAction> Actions { get; init; } = AllActions;
    }
}
