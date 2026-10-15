using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class QueryApprovalRequest : BaseEntity
{
    public int QueryId { get; set; }

    public int QueryVersionId { get; set; }

    public ApprovalStatus Status { get; set; }

    public string? RequestedByUserId { get; set; }

    public string? RequestedByUserName { get; set; }

    public string? ReviewedByUserId { get; set; }

    public string? ReviewedByUserName { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? ReviewComment { get; set; }

    public string? ChangeSummary { get; set; }

    /// <summary>
    /// The query's active version when the change was submitted (null when it had none, and for requests recorded
    /// before Beacon kept it): the change is approved only while that version is still the active one.
    /// </summary>
    public int? BaseVersionId { get; set; }

    /// <summary>The content hash of the version an approval applied (<see cref="QueryVersion.ContentHash"/>); null until approved.</summary>
    public string? ApprovedContentHash { get; set; }

    /// <summary>
    /// Besides the requester, the people who shaped the change (<c>Users.ExternalId</c>): for an AI actor's change made by
    /// approving one of its plans, the plan's reviewer and everyone who asked for a revision of it. None of them approves
    /// the change. Null for a person's own edit and for requests recorded before Beacon kept it.
    /// </summary>
    public string[]? ContributorUserIds { get; set; }

    // Navigation
    public Query Query { get; set; } = null!;
    public QueryVersion QueryVersion { get; set; } = null!;
}
