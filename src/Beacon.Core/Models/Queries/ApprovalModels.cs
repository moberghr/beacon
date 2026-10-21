using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class ApprovalRequestSummary
{
    public int Id { get; set; }
    public int QueryId { get; set; }
    public string QueryName { get; set; } = null!;
    public int VersionNumber { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? RequestedByUserName { get; set; }
    public DateTime CreatedTime { get; set; }
    public string? ChangeSummary { get; set; }
}

public class ApprovalRequestDetail
{
    public int Id { get; set; }
    public int QueryId { get; set; }
    public string QueryName { get; set; } = null!;
    public int QueryVersionId { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? RequestedByUserId { get; set; }
    public string? RequestedByUserName { get; set; }
    public string? ReviewedByUserName { get; set; }

    /// <summary>The reviewer's <c>Users.Id</c>, which names them when the label is a display name they can change; null while pending or when the user no longer resolves.</summary>
    public int? ReviewedByBeaconUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public string? ChangeSummary { get; set; }
    public DateTime CreatedTime { get; set; }
    public QueryVersionDetail ProposedVersion { get; set; } = null!;
    public QueryVersionDetail? CurrentActiveVersion { get; set; }
    public QueryVersionDiff? AutoDiff { get; set; }

    /// <summary>
    /// The proposed version's content hash (stored, or computed from its snapshot for a version recorded before Beacon kept
    /// one; null when the snapshot cannot be read). Approving sends it back as the content it approves, so the decision
    /// applies exactly what was shown (409 otherwise).
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>The caller requested this change, by any id their request presents.</summary>
    public bool IsOwnRequest { get; set; }

    /// <summary>
    /// The request is pending and the caller may approve it: they neither requested nor shaped it (unless the host allows
    /// self-approval), it records a requester, the version it was made on and its content, and the query's active version
    /// is still that version. The Approver permission is checked separately; the server decides again on approval.
    /// </summary>
    public bool MayApprove { get; set; }

    /// <summary>Why the caller cannot approve this pending request; null when <see cref="MayApprove"/>, or when it is not pending.</summary>
    public string? ApproveBlockedReason { get; set; }

    /// <summary>The request is pending and the query's active version is no longer the one the change was made on.</summary>
    public bool IsStale { get; set; }
}

/// <summary>
/// A decision on an approval request: the request, its new status, and who requested and decided the change, by
/// <c>Users.ExternalId</c> — the id the realtime hub names a signed-in user by.
/// </summary>
public sealed record QueryApprovalDecision(int RequestId, ApprovalStatus Status, string? RequesterUserId, string? ReviewerUserId);
