namespace Beacon.Core.Authorization;

/// <summary>
/// The projects a caller may act in for one <see cref="ProjectAction"/>, as <see cref="ProjectAccessScope.For"/> returns
/// it.
/// </summary>
/// <param name="Ids">
/// The live projects the action is allowed in. Null: every live project, and every data source (an Admin, or a
/// deployment where no project is restricted and every data source is openly reachable).
/// </param>
/// <param name="IncludesOpen">
/// Whether the openly reachable data sources count as well: those linked to a live open project, and the unlinked ones
/// that stay open (<c>DataSource.OpenWhenUnlinked</c>). Never for a caller restricted to a project list.
/// </param>
/// <param name="NotOpenDataSourceIds">
/// The data sources that are not openly reachable: linked only to restricted or archived projects, or unlinked and
/// closed. With <paramref name="IncludesOpen"/>, a data source not in this list is reachable.
/// </param>
public sealed record ProjectSet(IReadOnlyList<int>? Ids, bool IncludesOpen, IReadOnlyList<int> NotOpenDataSourceIds)
{
    /// <summary>Every live project and data source: the set every action reaches where nothing is restricted.</summary>
    public static ProjectSet All { get; } = new(null, true, []);

    /// <summary>
    /// Every live project and data source, and what only Admins reach (a row recorded without a project): the set of an
    /// unrestricted caller (an Admin, work without a request).
    /// </summary>
    public static ProjectSet Unrestricted { get; } = new(null, true, []) { IsUnrestricted = true };

    /// <summary>No project and no data source.</summary>
    public static ProjectSet Empty { get; } = new([], false, []);

    /// <summary>
    /// True for an unrestricted caller (<see cref="Unrestricted"/>): besides every project, it reaches what only Admins
    /// do. False on the open fast path (<see cref="All"/>), where a row recorded without a project stays an Admin's.
    /// </summary>
    public bool IsUnrestricted { get; init; }

    /// <summary>
    /// True for a caller confined to a project list (a scoped caller restricted to projects): what no project holds stays
    /// hidden from it, namely queries without steps and runs recorded before Beacon kept their data sources.
    /// </summary>
    public bool IsConfinedToList { get; init; }

    /// <summary>
    /// The caller's user id (<c>Users.ExternalId</c>) when the action reaches the queries the caller owns that have no
    /// steps (and the work derived from them); null otherwise. Such a query reads no data source, so no project holds
    /// it: only its owner (<see cref="ProjectAction.Read"/> and <see cref="ProjectAction.Write"/>) and Admins reach it.
    /// </summary>
    public string? QueryOwnerUserId { get; init; }

    /// <summary>True when <paramref name="projectId"/> is one of <see cref="Ids"/>, or <see cref="Ids"/> is null.</summary>
    public bool Contains(int projectId)
    {
        return Ids == null || Ids.Contains(projectId);
    }

    /// <summary>
    /// True when <paramref name="dataSourceId"/> is reachable through the openly reachable sources alone. Reaching it
    /// through one of <see cref="Ids"/> depends on the project's links, which a caller checks against the database.
    /// </summary>
    public bool ReachesOpenly(int dataSourceId)
    {
        return Ids == null || (IncludesOpen && !NotOpenDataSourceIds.Contains(dataSourceId));
    }

    /// <summary>
    /// True when a query without steps owned by <paramref name="ownerUserId"/> is reached: everywhere when
    /// <see cref="Ids"/> is null, otherwise only when its owner is the caller and the action reaches the queries the
    /// caller owns (<see cref="QueryOwnerUserId"/>).
    /// </summary>
    public bool ReachesStepLessQueryOf(string? ownerUserId)
    {
        return Ids == null || (QueryOwnerUserId != null && ownerUserId == QueryOwnerUserId);
    }
}
