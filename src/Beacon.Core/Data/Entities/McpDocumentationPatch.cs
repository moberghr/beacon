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

    /// <summary>The learned pattern the learning job proposed the patch from; no foreign key. Null for a patch from before.</summary>
    public int? SourcePatternId { get; set; }

    /// <summary>The model that proposed the patch when the description generator did; null for any other patch.</summary>
    public string? GeneratedByModel { get; set; }
}
