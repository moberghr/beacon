using Beacon.Core.Data.Enums;

namespace Beacon.Core;

/// <summary>
/// The change approval workflow: query changes, and (each with its own switch, on by default) migration jobs, Custom SQL
/// rules and recipient destinations, and (each with its own switch, off by default) data-source connections, the LLM
/// connection and grants of the Admin role. Opt-in (<see cref="Enabled"/>): a host cannot tell a new installation from an
/// existing one, so turning it on is the host's decision; a startup note says when it is off.
/// </summary>
public class ApprovalWorkflowOptions
{
    private const int MinLlmConnectionRequestDays = 1;
    private const int MaxLlmConnectionRequestDays = 30;

    private int _llmConnectionRequestDays = 7;

    /// <summary>
    /// Enable approval workflow for query changes. Default: false (backward compatible, opt-in).
    /// When enabled, every change to a query's executable content (its steps, their order, names, data sources,
    /// parameters and SQL, and the final query) — an edit, a version restore, an AI actor's refinement — waits for a
    /// second person's approval before it goes live, and subscription runs execute only the reviewed content.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Let the person who requested a change (or shaped it: an AI plan's reviewer and revision requesters) approve it
    /// themselves, and publish as an MCP tool a query whose active version they requested or approved; also lets an AI
    /// actor's creator and a plan's revision requesters approve its plans while the workflow is on, and lets a change be
    /// submitted by a caller Beacon cannot name. Default: false (four-eyes). For a host with a single person who can
    /// approve; a startup Warning says it is on, and with it off the host warns when fewer than two users can approve. It
    /// never applies to a grant of the Admin role (<see cref="CoversAdminRoleGrants"/>), and neither lets a caller Beacon
    /// cannot name request one.
    /// </summary>
    public bool AllowSelfApproval { get; set; } = false;

    /// <summary>
    /// Strict mode: with the workflow on, a subscription runs only when its query's active version carries an approved
    /// request for exactly its content (the approval recorded the version's content hash), and the stored SQL is that
    /// content; saved-query MCP tools then run only such a version too. Default: false, which still refuses a run whose
    /// stored SQL differs from the active version, runs a query never versioned as stored (each run logs a Warning) and
    /// lets an MCP tool run a version approved before Beacon recorded hashes (named at startup). Recommended for regulated
    /// deployments.
    /// </summary>
    public bool RequireApprovedVersionToRun { get; set; } = false;

    /// <summary>
    /// With the workflow on, a migration job created (or whose definition — source, steps, destination, mode,
    /// transformation — changed) runs only once a second person approved exactly that definition; until then every run of
    /// it, manual or scheduled, is refused and recorded as failed. Default: true. Jobs stored before Beacon reviewed
    /// migration jobs, or last saved with this switch off, run as before.
    /// </summary>
    public bool CoversMigrationJobs { get; set; } = true;

    /// <summary>
    /// With the workflow on, a data contract's Custom SQL rule that is new, or whose SQL or data source changed, stays out
    /// of every evaluation until a second person approved exactly that SQL on that data source. Default: true. Rules stored
    /// before Beacon reviewed them, or last saved with this switch off, run as before.
    /// </summary>
    public bool CoversCustomSqlRules { get; set; } = true;

    /// <summary>
    /// With the workflow on, a recipient created (or whose type, destination or headers changed) receives nothing until a
    /// second person approved exactly where it delivers: deliveries skip it meanwhile, each skip logged. Default: true.
    /// Attaching an existing recipient to a subscription or data contract is not reviewed. Recipients stored before Beacon
    /// reviewed them, or last saved with this switch off, deliver as before.
    /// </summary>
    public bool CoversRecipientDestinations { get; set; } = true;

    /// <summary>
    /// With the workflow on, a data source created, or whose connection changed in anything but its secrets (host, port,
    /// database, login, or any other connection setting), makes no connection until a second person approved exactly that
    /// connection; a change of its secrets alone (a rotated password) applies at once. Default: false. Sources stored before
    /// Beacon reviewed connections, last saved with this switch off, or managed by the host connect as before.
    /// </summary>
    public bool CoversDataSourceConnections { get; set; } = false;

    /// <summary>
    /// With the workflow on, a change of the stored LLM connection's provider, endpoint, region or authentication mode is
    /// staged, encrypted, until a live Admin other than the requester approves it; the stored connection stays in use
    /// meanwhile. A change of credentials or models alone applies at once. Default: false.
    /// </summary>
    public bool CoversLlmConnection { get; set; } = false;

    /// <summary>
    /// With the workflow on, giving a user the Admin role (creating a user with it, or adding it to a user's roles) does not
    /// apply it: the rest of the save goes through, and the grant waits until a live Admin other than the requester approves
    /// it, whatever <see cref="AllowSelfApproval"/> says. Removing the Admin role and granting a functional role apply at
    /// once. A user provisioned at a first sign-in is never given the Admin role while this is on. Default: false.
    /// </summary>
    public bool CoversAdminRoleGrants { get; set; } = false;

    /// <summary>
    /// How many days a staged LLM connection change waits for a decision before it expires and its staged values are wiped.
    /// Default: 7; values are held between 1 and 30.
    /// </summary>
    public int LlmConnectionRequestDays
    {
        get => _llmConnectionRequestDays;
        set => _llmConnectionRequestDays = Math.Clamp(value, MinLlmConnectionRequestDays, MaxLlmConnectionRequestDays);
    }

    /// <summary>True when the workflow is on and covers changes of <paramref name="kind"/>.</summary>
    public bool Covers(ChangeApprovalKind kind)
    {
        return Enabled && kind switch
        {
            ChangeApprovalKind.MigrationJob => CoversMigrationJobs,
            ChangeApprovalKind.CustomSqlRules => CoversCustomSqlRules,
            ChangeApprovalKind.RecipientDestination => CoversRecipientDestinations,
            ChangeApprovalKind.DataSourceConnection => CoversDataSourceConnections,
            ChangeApprovalKind.LlmConnection => CoversLlmConnection,
            ChangeApprovalKind.AdminRoleGrant => CoversAdminRoleGrants,
            _ => false
        };
    }
}
