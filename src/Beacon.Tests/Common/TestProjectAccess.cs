using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// Project access for handler tests: an evaluator that answers a fixed <see cref="ProjectAccessScope"/>, scopes built
/// the way the evaluator builds them, and an actor accessor for a stored, active user.
/// </summary>
internal static class TestProjectAccess
{
    private static readonly ProjectAction[] ReadActions = [ProjectAction.Read, ProjectAction.Run];

    private static readonly ProjectAction[] EditorActions =
        [ProjectAction.Read, ProjectAction.Run, ProjectAction.Query, ProjectAction.Write];

    private static readonly ProjectAction[] AllActions = Enum.GetValues<ProjectAction>();

    public static IProjectAccessEvaluator Evaluator(ProjectAccessScope scope)
    {
        var evaluator = new Mock<IProjectAccessEvaluator>();
        evaluator
            .Setup(x => x.GetScopeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
        evaluator
            .Setup(x => x.GetScopeAsync(It.IsAny<BeaconActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);

        return evaluator.Object;
    }

    /// <summary>
    /// The scope the evaluator gives a caller where every project and data source is open: a signed-in caller reads
    /// everything; a caller restricted to projects (<c>allowed_projects</c>) reads within them; an anonymous caller, and a
    /// scoped caller without a restriction and without a user behind it, read nothing.
    /// </summary>
    public static IProjectAccessEvaluator ForPrincipal(ClaimsPrincipal? caller)
    {
        if (caller?.Identity?.IsAuthenticated != true)
        {
            return Evaluator(ProjectAccessScope.None);
        }

        var restriction = ProjectRestriction.Of(caller);
        if (restriction != null)
        {
            return Evaluator(ReadableIn(restriction.ToArray()));
        }

        return Evaluator(BeaconScopes.IsScopedCaller(caller) ? ProjectAccessScope.None : Unrestricted);
    }

    /// <summary>
    /// A non-Admin caller on the fast path: no project is restricted or archived and every data source is openly
    /// reachable, so every action but managing members reaches everything (the list filters add nothing).
    /// </summary>
    public static ProjectAccessScope FastPath => new(
        false,
        AllActions.ToDictionary(x => x, x => x == ProjectAction.ManageMembers ? ProjectSet.Empty : ProjectSet.All),
        new Dictionary<int, ProjectGrant>());

    /// <summary>An Admin: everything.</summary>
    public static ProjectAccessScope Unrestricted => ProjectAccessScope.Unrestricted;

    /// <summary>A caller confined to <paramref name="projectIds"/> (a key with a project list) as a Viewer: read and run there.</summary>
    public static ProjectAccessScope ReadableIn(params int[] projectIds)
    {
        return Scope(projectIds.ToDictionary(x => x, _ => ProjectRole.Viewer), includesOpen: false, notOpen: [], confinedToList: true);
    }

    /// <summary>
    /// A caller with the given roles per project, reaching the openly reachable data sources (all but
    /// <paramref name="notOpen"/>) as an Editor when <paramref name="includesOpen"/>; confined to those projects (a
    /// scoped caller with a project list) when <paramref name="confinedToList"/>; reaching the queries without steps
    /// that <paramref name="queryOwnerUserId"/> owns, for Read and Write, when set.
    /// </summary>
    public static ProjectAccessScope Scope(
        IReadOnlyDictionary<int, ProjectRole> roles,
        bool includesOpen,
        IReadOnlyList<int> notOpen,
        bool confinedToList = false,
        string? queryOwnerUserId = null)
    {
        var grants = roles.ToDictionary(
            x => x.Key,
            x => new ProjectGrant(x.Value, ActionsOf(x.Value).ToHashSet()));
        var sets = AllActions.ToDictionary(
            x => x,
            x => new ProjectSet(
                grants
                    .Where(y => y.Value.Actions.Contains(x))
                    .Select(y => y.Key)
                    .Order()
                    .ToList(),
                includesOpen && x != ProjectAction.ManageMembers,
                notOpen)
            {
                IsConfinedToList = confinedToList,
                QueryOwnerUserId = x is ProjectAction.Read or ProjectAction.Write ? queryOwnerUserId : null
            });

        return new ProjectAccessScope(false, sets, grants);
    }

    /// <summary>
    /// The authorizer behind the project authorization behaviour, for <paramref name="scope"/>. Unrestricted and fast-path
    /// scopes never read the database; other scopes resolve resources with <paramref name="resolver"/> (default: the real
    /// resolver) on <paramref name="factory"/>. The caller holds <paramref name="permissions"/> (default: none).
    /// </summary>
    public static ProjectRequestAuthorizer Authorizer(
        ProjectAccessScope scope,
        IProjectResourceResolver? resolver = null,
        IDbContextFactory<BeaconContext>? factory = null,
        IBeaconActorAccessor? actors = null,
        ILogger<ProjectRequestAuthorizer>? logger = null,
        IBeaconPermissionAccessor? permissions = null)
    {
        return new ProjectRequestAuthorizer(
            Evaluator(scope),
            resolver ?? new ProjectResourceResolver(),
            factory ?? new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict).Object,
            actors ?? Actors(),
            permissions ?? TestPermissions.None,
            logger ?? NullLogger<ProjectRequestAuthorizer>.Instance);
    }

    /// <summary>The real actor accessor over <paramref name="httpContextAccessor"/>, in a scope nobody marked as system work.</summary>
    public static BeaconActorAccessor ActorAccessor(
        Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<BeaconContext> factory)
    {
        return new BeaconActorAccessor(
            httpContextAccessor,
            factory,
            new SystemWorkScope(),
            new RunAsScope(httpContextAccessor),
            new UnmarkedSystemWorkLog(TimeProvider.System, NullLogger<UnmarkedSystemWorkLog>.Instance));
    }

    /// <summary>An execute-time check that allows every run: for tests of what a run does, not who may make it.</summary>
    public static IExecutionAccessCheck ExecutionAllowed()
    {
        var check = new Mock<IExecutionAccessCheck>();
        check
            .Setup(x => x.AllowsAsync(It.IsAny<ProjectAction>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return check.Object;
    }

    /// <summary>Resolves every request to a stored, active, non-Admin user.</summary>
    public static IBeaconActorAccessor Actors(int beaconUserId = 3, string externalId = "ext-ada")
    {
        var actor = new BeaconActor(externalId, false)
        {
            Kind = BeaconPrincipalKind.User,
            BeaconUserId = beaconUserId,
            IsActiveUser = true,
            RoleLevel = 2
        };
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    private static IEnumerable<ProjectAction> ActionsOf(ProjectRole role)
    {
        return role switch
        {
            ProjectRole.Viewer => ReadActions,
            ProjectRole.Editor => EditorActions,
            _ => AllActions
        };
    }
}
