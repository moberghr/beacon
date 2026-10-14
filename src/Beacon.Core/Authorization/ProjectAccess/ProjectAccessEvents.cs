using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Security log events for changes to project access: membership grants, role changes and revocations, a project's
/// access mode, data sources linked into or unlinked from projects, and the repository actions that read code with a
/// stored credential (scans) or change that credential; and for refused access: an action refused on a resource the
/// caller may see (403), a resource answered as not found (404), and scheduled work skipped because its owner may no
/// longer run it; a project created open because its creator could not be its member; a user created; and a user's
/// roles assigned or removed; a data source marked or unmarked as a migration write target; a read-only query on SQL
/// Server that ended the transaction it ran in; every session of a user signed out ("sign out everywhere"); a sign-in
/// session ended when it was revalidated; while local login is off, a password sign-in by a break-glass account and a
/// password sign-in refused; the Data Protection key ring found without a repository, acknowledged as unencrypted, or
/// holding keys stored without encryption (revoked and replaced); stored values moved from a previous encryption key to
/// the current one; and a request to the LLM provider whose prompt carries row-derived data (sample values, value
/// matches, column values, result rows). Ids only (§1.11). Public so every Beacon assembly logs them with the same ids.
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
    public static readonly EventId ScheduledWorkSkipped = new(7111, nameof(ScheduledWorkSkipped));
    public static readonly EventId ProjectCreatedOpen = new(7112, nameof(ProjectCreatedOpen));
    public static readonly EventId UserRolesChanged = new(7113, nameof(UserRolesChanged));
    public static readonly EventId UserCreated = new(7114, nameof(UserCreated));
    public static readonly EventId MigrationWritesChanged = new(7115, nameof(MigrationWritesChanged));
    public static readonly EventId ReadOnlyTransactionEnded = new(7116, nameof(ReadOnlyTransactionEnded));
    public static readonly EventId UserSessionsRevoked = new(7117, nameof(UserSessionsRevoked));
    public static readonly EventId SessionEnded = new(7118, nameof(SessionEnded));
    public static readonly EventId BreakGlassSignIn = new(7119, nameof(BreakGlassSignIn));
    public static readonly EventId PasswordSignInRefused = new(7120, nameof(PasswordSignInRefused));
    public static readonly EventId DataProtectionKeyRing = new(7121, nameof(DataProtectionKeyRing));
    public static readonly EventId StoredValuesReEncrypted = new(7122, nameof(StoredValuesReEncrypted));
    public static readonly EventId RowDataSentToLlm = new(7123, nameof(RowDataSentToLlm));
}
