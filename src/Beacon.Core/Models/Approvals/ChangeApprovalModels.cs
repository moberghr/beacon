using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Approvals;

/// <summary>
/// A pending change of a migration job, a data contract's Custom SQL rules, a recipient, a data source's connection or the
/// LLM connection, as listed.
/// </summary>
public sealed record ChangeApprovalSummary
{
    public int Id { get; init; }

    public ChangeApprovalKind Kind { get; init; }

    public int ResourceId { get; init; }

    /// <summary>The resource's name when the change was submitted.</summary>
    public string ResourceName { get; init; } = string.Empty;

    public ApprovalStatus Status { get; init; }

    public string? RequestedByUserName { get; init; }

    public DateTime CreatedTime { get; init; }

    public string? ChangeSummary { get; init; }
}

/// <summary>
/// A change request with what it changes: for its <see cref="Kind"/>, the resource before (null for a resource the change
/// creates) and as proposed. Destinations are masked as the recipients page masks them, a connection shows its host, port,
/// database and login and the names of the settings it changes, and the LLM connection its endpoint's scheme and host
/// with fingerprints; no secret is ever returned.
/// </summary>
public sealed record ChangeApprovalDetail
{
    public int Id { get; init; }

    public ChangeApprovalKind Kind { get; init; }

    public int ResourceId { get; init; }

    /// <summary>The resource's name now, or as submitted when it no longer exists.</summary>
    public string ResourceName { get; init; } = string.Empty;

    /// <summary>The resource still exists (a deleted one's change can be rejected, not approved).</summary>
    public bool ResourceExists { get; init; }

    public ApprovalStatus Status { get; init; }

    public string? RequestedByUserName { get; init; }

    public string? ReviewedByUserName { get; init; }

    /// <summary>The reviewer's <c>Users.Id</c>, which names them when the label is a display name they can change; null while pending or when the user no longer resolves.</summary>
    public int? ReviewedByBeaconUserId { get; init; }

    public DateTime? ReviewedAt { get; init; }

    public string? ReviewComment { get; init; }

    public string? ChangeSummary { get; init; }

    public DateTime CreatedTime { get; init; }

    public MigrationJobDefinitionView? PreviousMigrationJob { get; init; }

    public MigrationJobDefinitionView? ProposedMigrationJob { get; init; }

    public CustomSqlRulesView? PreviousCustomSqlRules { get; init; }

    public CustomSqlRulesView? ProposedCustomSqlRules { get; init; }

    public RecipientDestinationView? PreviousRecipientDestination { get; init; }

    public RecipientDestinationView? ProposedRecipientDestination { get; init; }

    /// <summary>A data source's connection as it was stored before the change; null for a source the change creates.</summary>
    public DataSourceConnectionView? PreviousDataSourceConnection { get; init; }

    public DataSourceConnectionView? ProposedDataSourceConnection { get; init; }

    /// <summary>The LLM connection the change was made from (<see cref="LlmConnectionView.FromHostConfiguration"/> when none was stored).</summary>
    public LlmConnectionView? PreviousLlmConnection { get; init; }

    public LlmConnectionView? ProposedLlmConnection { get; init; }

    /// <summary>
    /// For a data source's connection, the projects it is linked to now, read when the detail is shown: links are not part
    /// of what the request proposes, so one made or removed since does not make it stale. Empty for every other kind.
    /// </summary>
    public IReadOnlyList<ChangeApprovalProject> CurrentProjects { get; init; } = [];

    /// <summary>
    /// The hash of what this detail shows as proposed: approving sends it back, so the decision applies exactly what the
    /// reviewer was shown (409 otherwise).
    /// </summary>
    public string PayloadHash { get; init; } = string.Empty;

    /// <summary>The caller requested this change, by any id their request presents.</summary>
    public bool IsOwnRequest { get; init; }

    /// <summary>
    /// The request is pending and the caller may approve it: they did not request it (unless the host allows
    /// self-approval), it records a requester, and the resource still holds exactly the definition it proposes. The
    /// Approver permission is checked separately; the server decides again on approval.
    /// </summary>
    public bool MayApprove { get; init; }

    /// <summary>Why the caller cannot approve this pending request; null when <see cref="MayApprove"/>, or when it is not pending.</summary>
    public string? ApproveBlockedReason { get; init; }

    /// <summary>The request is pending and the resource no longer holds the definition it proposes (or no longer exists).</summary>
    public bool IsStale { get; init; }

    /// <summary>
    /// A recipient change whose before and after look the same — type, destination as shown, header names — while the
    /// definition differs (the fingerprints differ): the change lies only in a masked part, such as a token or a header
    /// value. Approving it requires a comment (<see cref="CommentRequiredToApprove"/>).
    /// </summary>
    public bool ChangeHiddenByMask { get; init; }

    /// <summary>
    /// Approving this request requires a comment: what changed cannot be seen (a recipient change that lies only in a masked
    /// part, <see cref="ChangeHiddenByMask"/>, or a data-source connection its connector cannot show setting by setting,
    /// <see cref="DataSourceConnectionView.SecretOnlyDefinition"/>), so the reviewer records how they checked it.
    /// </summary>
    public bool CommentRequiredToApprove { get; init; }
}

/// <summary>A project a data source under review is linked to, by id and name.</summary>
public sealed record ChangeApprovalProject
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// A migration job's definition: the source data source, the source query (SQL, or JSON steps), the destination, the
/// write mode and the transformation script. The data source names are filled in when it is shown.
/// </summary>
public sealed record MigrationJobDefinitionView
{
    public int SourceDataSourceId { get; init; }

    public string? SourceDataSourceName { get; init; }

    public string QueryText { get; init; } = string.Empty;

    public int DestinationDataSourceId { get; init; }

    public string? DestinationDataSourceName { get; init; }

    public string DestinationTable { get; init; } = string.Empty;

    public MigrationMode Mode { get; init; }

    public string? TransformationScript { get; init; }
}

/// <summary>A data contract's Custom SQL rules and the data source they run on (its name filled in when it is shown).</summary>
public sealed record CustomSqlRulesView
{
    public int DataSourceId { get; init; }

    public string? DataSourceName { get; init; }

    public IReadOnlyList<CustomSqlRuleView> Rules { get; init; } = [];
}

/// <summary>One Custom SQL rule: its name, its SQL, and whether the change asks for its approval.</summary>
public sealed record CustomSqlRuleView
{
    public string Name { get; init; } = string.Empty;

    public string Sql { get; init; } = string.Empty;

    /// <summary>The rule waits for this change's approval (new, or its SQL or data source changed); false for one already approved, or not governed.</summary>
    public bool AwaitsApproval { get; init; }
}

/// <summary>
/// Where a recipient delivers, as a reviewer is shown it: its type; what identifies its destination (the host, a known
/// provider's documented non-secret ids such as Slack's workspace and webhook ids or a Teams workflow's id, a Jira site,
/// email addresses) with everything else masked (every other path segment, user info, query values, a fragment, a token
/// or signature); its header names with a fingerprint of each value; and a
/// fingerprint of the whole definition. A fingerprint is a short keyed digest: equal fingerprints mean equal values, and
/// it reveals nothing about the value, so it can be compared out of band (the recipients page shows the same ones to
/// those who may change the recipient).
/// </summary>
public sealed record RecipientDestinationView
{
    public NotificationType NotificationType { get; init; }

    public string Destination { get; init; } = string.Empty;

    public IReadOnlyList<string> HeaderNames { get; init; } = [];

    /// <summary>The custom headers by name, each with the fingerprint of its value.</summary>
    public IReadOnlyList<RecipientHeaderView> Headers { get; init; } = [];

    /// <summary>The fingerprint of the whole definition (type, destination and headers); null when it cannot be read.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The stored destination or headers could not be decrypted, so nothing of them is shown.</summary>
    public bool Unreadable { get; init; }
}

/// <summary>A custom header's name and the fingerprint of its value (never the value).</summary>
public sealed record RecipientHeaderView
{
    public string Name { get; init; } = string.Empty;

    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>
/// A data source's connection, as a reviewer is shown it: its name, type and engine; the host, port, database and login it
/// connects with; the names (never the values) of the other connection settings the change sets or changes; a keyed
/// fingerprint of the whole definition; and how the connection is protected. No secret and no other setting value is ever
/// shown.
/// </summary>
public sealed record DataSourceConnectionView
{
    public string Name { get; init; } = string.Empty;

    public DataSourceType DataSourceType { get; init; }

    public DatabaseEngineType? DatabaseEngineType { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Database { get; init; }

    /// <summary>The user name the connection signs in with, or its authentication mode when it has none.</summary>
    public string? Login { get; init; }

    /// <summary>The names of the connection settings the change sets or changes (every setting of a new source), ordinally sorted.</summary>
    public IReadOnlyList<string> ChangedSettings { get; init; } = [];

    /// <summary>
    /// The connection cannot be read setting by setting (its connector gives no definition): the definition is the whole
    /// connection data, secrets included, shown only as its <see cref="Fingerprint"/>, and approving it requires a comment.
    /// </summary>
    public bool SecretOnlyDefinition { get; init; }

    /// <summary>A short keyed fingerprint of the whole definition, to compare it out of band.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The connection authenticates the server over TLS.</summary>
    public bool AuthenticatesServer { get; init; }

    /// <summary>The engine enforces read-only on the connection itself, besides Beacon's own checks.</summary>
    public bool DatabaseReadOnlyEnforced { get; init; }

    /// <summary>The change points the source at another engine, host, port or database.</summary>
    public bool IsTargetChange { get; init; }
}

/// <summary>
/// The stored LLM connection, as a reviewer is shown it: the provider, the endpoint as scheme and host only with a keyed
/// fingerprint of the whole endpoint, the region, the authentication mode and the models, the fingerprint of the connection
/// the change is based on, and a fingerprint of each credential; never a credential or the endpoint's path.
/// </summary>
public sealed record LlmConnectionView
{
    /// <summary>The connection is the host's configuration (nothing stored yet), so no value is shown.</summary>
    public bool FromHostConfiguration { get; init; }

    public AiProvider? Provider { get; init; }

    /// <summary>The endpoint's scheme, host and port; its path and query only show in <see cref="EndpointFingerprint"/>.</summary>
    public string? Endpoint { get; init; }

    public string? EndpointFingerprint { get; init; }

    public string? Region { get; init; }

    public BedrockAuthMode BedrockAuthMode { get; init; }

    public string? Model { get; init; }

    public string? FastModel { get; init; }

    /// <summary>The fingerprint of the stored connection the change was made from.</summary>
    public string? BasedOn { get; init; }

    public IReadOnlyList<LlmCredentialView> Credentials { get; init; } = [];
}

/// <summary>A credential of the LLM connection: its name and the fingerprint of its value (never the value).</summary>
public sealed record LlmCredentialView
{
    public string Name { get; init; } = string.Empty;

    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>
/// The last decision on a governed resource's change, shown with the resource while it waits for approval: whether it
/// was rejected (change it and save again) or superseded, when, and the reviewer's comment.
/// </summary>
public sealed record ChangeApprovalOutcome
{
    public int RequestId { get; init; }

    public ApprovalStatus Status { get; init; }

    public string? Comment { get; init; }

    public DateTime? DecidedAt { get; init; }
}

/// <summary>
/// A decision on a change request: the request, its new status, and who requested and decided the change, by
/// <c>Users.ExternalId</c> — the id the realtime hub names a signed-in user by.
/// </summary>
public sealed record ChangeApprovalDecision(int RequestId, ApprovalStatus Status, string? RequesterUserId, string? ReviewerUserId);
