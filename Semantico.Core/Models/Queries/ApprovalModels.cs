using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Queries;

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
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public string? ChangeSummary { get; set; }
    public DateTime CreatedTime { get; set; }
    public QueryVersionDetail ProposedVersion { get; set; } = null!;
    public QueryVersionDetail? CurrentActiveVersion { get; set; }
    public QueryVersionDiff? AutoDiff { get; set; }
}
