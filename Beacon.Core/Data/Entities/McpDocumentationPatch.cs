using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class McpDocumentationPatch : BaseEntity
{
    public int ProjectId { get; set; }
    public int DataSourceId { get; set; }

    public McpDocPatchTarget TargetType { get; set; }
    public string TargetIdentifier { get; set; } = null!;
    public string? CurrentContent { get; set; }
    public string ProposedContent { get; set; } = null!;
    public string Reasoning { get; set; } = null!;

    public int SupportingSignalCount { get; set; }

    public McpDocPatchStatus Status { get; set; }
    public int? AppliedByUserId { get; set; }
    public DateTime? AppliedAt { get; set; }
}
