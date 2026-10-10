namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Where a <see cref="ProjectResource"/> lives, as <see cref="IProjectResourceResolver"/> reads it: in a project
/// (<see cref="ProjectId"/>), or reachable through the data sources it reads (<see cref="SourceSets"/>: one project in
/// the caller's set must hold every source of each set), or missing, or an Admin's alone. With its recorded owner, when
/// its kind records one, and the data sources whose shared state it writes.
/// </summary>
internal sealed record ResolvedProjectResource
{
    public static ResolvedProjectResource Missing { get; } = new() { IsMissing = true };

    public static ResolvedProjectResource AdminOnly { get; } = new() { IsAdminOnly = true };

    public bool IsMissing { get; init; }

    public bool IsAdminOnly { get; init; }

    /// <summary>The project the resource lives in, for a resource addressed by its project.</summary>
    public int? ProjectId { get; init; }

    /// <summary>
    /// For a resource addressed by its data sources: each set must be reachable (a query version and its query are two
    /// sets). An empty set (a query without steps) is no project's: it is reachable by the owner of the query it comes
    /// from (<see cref="QueryOwnerUserId"/>) and by Admins only.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> SourceSets { get; init; } = [];

    /// <summary>A run recorded before Beacon kept its data sources, read by its subscription's current query: never for a caller confined to a project list.</summary>
    public bool IsLegacyRun { get; init; }

    /// <summary>The recorded owner's user id (<c>Users.ExternalId</c>), for the kinds that record one.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>
    /// The owner (<c>Users.ExternalId</c>) of the query the resource's data sources come from (a query, its versions, a
    /// subscription or task on it): while the query has no steps, only this owner and Admins reach the resource.
    /// </summary>
    public string? QueryOwnerUserId { get; init; }

    /// <summary>The data sources whose shared state (metadata, relationships, descriptions) a change to the resource writes.</summary>
    public IReadOnlyList<int> SharedDataSourceIds { get; init; } = [];

    public static ResolvedProjectResource InProject(int projectId, IReadOnlyList<int>? sharedDataSourceIds = null) =>
        new() { ProjectId = projectId, SharedDataSourceIds = sharedDataSourceIds ?? [] };

    public static ResolvedProjectResource ThroughSources(IReadOnlyList<int> dataSourceIds, string? ownerUserId = null) =>
        new() { SourceSets = [dataSourceIds], OwnerUserId = ownerUserId, SharedDataSourceIds = dataSourceIds };

    /// <summary>A resource reached through its query's steps (a query, or a subscription or task on one), with the query's owner.</summary>
    public static ResolvedProjectResource ThroughQuery(IReadOnlyList<int> dataSourceIds, string? queryOwnerUserId, string? ownerUserId = null) =>
        ThroughSources(dataSourceIds, ownerUserId) with { QueryOwnerUserId = queryOwnerUserId };
}
