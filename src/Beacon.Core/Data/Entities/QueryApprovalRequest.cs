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

    // Navigation
    public Query Query { get; set; } = null!;
    public QueryVersion QueryVersion { get; set; } = null!;
}
