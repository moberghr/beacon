namespace Beacon.Core.Authorization;

/// <summary>
/// A caller's project access, as <see cref="IProjectAccessEvaluator"/> decides it once per request: the projects (and
/// openly reachable data sources) each <see cref="ProjectAction"/> is allowed in, and the caller's effective role per
/// project. A resource addressed by data sources is reachable through any project in <see cref="For"/> that holds all
/// of them, or through the openly reachable sources when every one of them is.
/// </summary>
public sealed class ProjectAccessScope
{
    private readonly IReadOnlyDictionary<ProjectAction, ProjectSet> _sets;
    private readonly IReadOnlyDictionary<int, ProjectGrant> _grants;

    internal ProjectAccessScope(
        bool isUnrestricted,
        IReadOnlyDictionary<ProjectAction, ProjectSet> sets,
        IReadOnlyDictionary<int, ProjectGrant> grants)
    {
        IsUnrestricted = isUnrestricted;
        _sets = sets;
        _grants = grants;
    }

    /// <summary>No project and no data source, for any action.</summary>
    public static ProjectAccessScope None { get; } = new(false, new Dictionary<ProjectAction, ProjectSet>(), new Dictionary<int, ProjectGrant>());

    /// <summary>Every project and data source, for every action: an Admin, or system work (no request, a marked scope).</summary>
    internal static ProjectAccessScope Unrestricted { get; } = new(true, new Dictionary<ProjectAction, ProjectSet>(), new Dictionary<int, ProjectGrant>());

    /// <summary>True for an Admin, and for system work (no request, a marked scope): every project and data source, for every action.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>Where <paramref name="action"/> is allowed (<see cref="ProjectSet.Unrestricted"/> for an unrestricted caller).</summary>
    public ProjectSet For(ProjectAction action)
    {
        if (IsUnrestricted)
        {
            return ProjectSet.Unrestricted;
        }

        return _sets.TryGetValue(action, out var set) ? set : ProjectSet.Empty;
    }

    /// <summary>
    /// Where <paramref name="action"/> is allowed for the recorded owner of the resource (a contract's or subscription's
    /// owner, an AI actor's creator, a query's lock holder): an owner may <see cref="ProjectAction.Manage"/> wherever it
    /// may <see cref="ProjectAction.Write"/>.
    /// </summary>
    public ProjectSet For(ProjectAction action, bool asRecordedOwner)
    {
        return asRecordedOwner && action == ProjectAction.Manage
            ? For(ProjectAction.Write)
            : For(action);
    }

    /// <summary>
    /// The caller's effective role in <paramref name="projectId"/>; null when it has none there. An unrestricted caller
    /// acts as <see cref="ProjectRole.ProjectAdmin"/> everywhere.
    /// </summary>
    public ProjectRole? RoleIn(int projectId)
    {
        if (IsUnrestricted)
        {
            return ProjectRole.ProjectAdmin;
        }

        return _grants.TryGetValue(projectId, out var grant) ? grant.Role : null;
    }

    /// <summary>
    /// Whether <paramref name="action"/> is allowed in <paramref name="projectId"/>. An unrestricted caller is allowed
    /// everywhere; whether the project exists and is live is the caller's to check (an archived project is missing).
    /// </summary>
    public bool Allows(int projectId, ProjectAction action)
    {
        return IsUnrestricted || (_grants.TryGetValue(projectId, out var grant) && grant.Actions.Contains(action));
    }

    /// <summary>As <see cref="Allows(int, ProjectAction)"/>, for the resource's recorded owner (see <see cref="For(ProjectAction, bool)"/>).</summary>
    public bool Allows(int projectId, ProjectAction action, bool asRecordedOwner)
    {
        return asRecordedOwner && action == ProjectAction.Manage
            ? Allows(projectId, ProjectAction.Write)
            : Allows(projectId, action);
    }
}

/// <summary>A caller's effective role in one live project and the actions it allows there.</summary>
internal sealed record ProjectGrant(ProjectRole Role, IReadOnlySet<ProjectAction> Actions);
