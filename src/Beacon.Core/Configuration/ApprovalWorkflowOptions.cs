namespace Beacon.Core;

/// <summary>
/// The query change approval workflow. Opt-in (<see cref="Enabled"/>): a host cannot tell a new installation from an
/// existing one, so turning it on is the host's decision; a startup note says when it is off.
/// </summary>
public class ApprovalWorkflowOptions
{
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
    /// approve; a startup Warning says it is on, and with it off the host warns when fewer than two users can approve.
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
}
