using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class QueryVersion : BaseEntity
{
    public int QueryId { get; set; }

    public int VersionNumber { get; set; }

    public string? Label { get; set; }

    public QueryVersionStatus Status { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string? FinalQuery { get; set; }

    /// <summary>
    /// JSON-serialized list of QueryStepSnapshot
    /// </summary>
    public string StepsJson { get; set; } = null!;

    public string? CreatedByUserId { get; set; }

    public string? ChangeSource { get; set; }

    public string? ChangeReason { get; set; }

    // Navigation
    public Query Query { get; set; } = null!;
}
