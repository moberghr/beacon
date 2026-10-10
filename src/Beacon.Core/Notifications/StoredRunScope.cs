using Beacon.Core.Authorization;

namespace Beacon.Core.Notifications;

/// <summary>
/// Which subscription runs (<c>QueryExecutionHistory</c>) a caller may read: their metadata, failure reason and stored
/// result rows. Every reader of stored runs is given one explicitly; there is no default. Built from the caller's
/// project access with <see cref="From"/>.
/// </summary>
public sealed class StoredRunScope
{
    private StoredRunScope(
        IReadOnlyList<int>? allowedProjectIds,
        bool includesOpen,
        IReadOnlyList<int> notOpenDataSourceIds,
        bool includesLegacyRuns)
    {
        AllowedProjectIds = allowedProjectIds;
        IncludesOpen = includesOpen;
        NotOpenDataSourceIds = notOpenDataSourceIds;
        IncludesLegacyRuns = includesLegacyRuns;
    }

    /// <summary>Every run: an Admin, or a caller in a deployment where every project and data source is open.</summary>
    public static StoredRunScope Unrestricted { get; } = new(null, true, [], true);

    /// <summary>No run: an anonymous caller, or a caller with no project access.</summary>
    public static StoredRunScope None { get; } = new([], false, [], false);

    /// <summary>
    /// Null for <see cref="Unrestricted"/>. Otherwise the projects that bound the readable runs: a run is readable when at
    /// least one of them holds every data source the run read. Empty: no run through a project.
    /// </summary>
    public IReadOnlyList<int>? AllowedProjectIds { get; }

    /// <summary>
    /// Whether a run is also readable when every data source it read is openly reachable (not in
    /// <see cref="NotOpenDataSourceIds"/>). Never for a caller restricted to a project list.
    /// </summary>
    public bool IncludesOpen { get; }

    /// <summary>The data sources that are not openly reachable, as the caller's project access read them.</summary>
    internal IReadOnlyList<int> NotOpenDataSourceIds { get; }

    /// <summary>
    /// Whether a run recorded before Beacon kept its data sources is readable, by its subscription's current query. Not
    /// for a caller confined to a project list.
    /// </summary>
    public bool IncludesLegacyRuns { get; }

    /// <summary>The runs a caller confined to <paramref name="projectIds"/> reads (no project: no run).</summary>
    public static StoredRunScope WithinProjects(IEnumerable<int> projectIds)
    {
        return new StoredRunScope(
            projectIds
                .Distinct()
                .ToList(),
            false,
            [],
            false);
    }

    /// <summary>The runs readable where a caller may <see cref="ProjectAction.Read"/>: <c>scope.For(ProjectAction.Read)</c>.</summary>
    public static StoredRunScope From(ProjectSet readable)
    {
        if (readable.Ids is { Count: 0 } && !readable.IncludesOpen)
        {
            return None;
        }

        return readable.Ids == null
            ? Unrestricted
            : new StoredRunScope(
                readable.Ids
                    .Distinct()
                    .ToList(),
                readable.IncludesOpen,
                readable.NotOpenDataSourceIds,
                !readable.IsConfinedToList);
    }
}
