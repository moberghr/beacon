namespace Beacon.Core.Notifications;

/// <summary>
/// Which subscription runs (<c>QueryExecutionHistory</c>) a caller may read: their metadata, failure reason and stored
/// result rows. Every reader of stored runs is given one explicitly; there is no default.
/// </summary>
public sealed class StoredRunScope
{
    private StoredRunScope(IReadOnlyList<int>? allowedProjectIds)
    {
        AllowedProjectIds = allowedProjectIds;
    }

    /// <summary>Every run: an authenticated caller that is not a scoped caller and carries no project restriction.</summary>
    public static StoredRunScope Unrestricted { get; } = new(null);

    /// <summary>No run: an anonymous caller, or a scoped caller with no project restriction to read runs by.</summary>
    public static StoredRunScope None { get; } = new([]);

    /// <summary>
    /// Null for <see cref="Unrestricted"/>. Otherwise the projects that bound the readable runs: a run is readable when at
    /// least one of them holds every data source the run read. Empty: no run.
    /// </summary>
    public IReadOnlyList<int>? AllowedProjectIds { get; }

    /// <summary>The runs readable within <paramref name="projectIds"/> (no project: no run).</summary>
    public static StoredRunScope WithinProjects(IEnumerable<int> projectIds)
    {
        return new StoredRunScope(projectIds
            .Distinct()
            .ToList());
    }
}
