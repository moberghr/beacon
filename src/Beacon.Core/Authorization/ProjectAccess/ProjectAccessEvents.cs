using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Security log events for changes to project access: membership grants, role changes and revocations, a project's
/// access mode, data sources linked into or unlinked from projects, and the repository actions that read code with a
/// stored credential (scans) or change that credential; and for refused access: an action refused on a resource the
/// caller may see (403), and a resource answered as not found (404). Ids only (§1.11). Public so every Beacon assembly
/// logs them with the same ids.
/// </summary>
public static class ProjectAccessEvents
{
    public static readonly EventId MemberAdded = new(7101, nameof(MemberAdded));
    public static readonly EventId MemberRoleChanged = new(7102, nameof(MemberRoleChanged));
    public static readonly EventId MemberRemoved = new(7103, nameof(MemberRemoved));
    public static readonly EventId AccessModeChanged = new(7104, nameof(AccessModeChanged));
    public static readonly EventId DataSourcesLinked = new(7105, nameof(DataSourcesLinked));
    public static readonly EventId RepositoryScanRequested = new(7106, nameof(RepositoryScanRequested));
    public static readonly EventId RepositoryTokenChanged = new(7107, nameof(RepositoryTokenChanged));
    public static readonly EventId AccessRefused = new(7108, nameof(AccessRefused));
    public static readonly EventId AccessHidden = new(7109, nameof(AccessHidden));
    public static readonly EventId DataSourceUnlinked = new(7110, nameof(DataSourceUnlinked));
}
