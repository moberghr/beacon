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

    /// <summary>
    /// The distinct data sources of the version's steps, recorded when the version is created. Null for versions
    /// recorded before Beacon kept them: their sources are read from <see cref="StepsJson"/>.
    /// </summary>
    public int[]? DataSourceIds { get; set; }

    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// Display name of the author at the time of the change, so history reads the same whichever
    /// sign-in produced <see cref="CreatedByUserId"/>.
    /// </summary>
    public string? CreatedByUserName { get; set; }

    public string? ChangeSource { get; set; }

    public string? ChangeReason { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the version's executable content — its steps in order with their names, data sources,
    /// SQL and parameters, and the final query — as <c>QueryContentHash</c> computes it. Null for versions recorded before
    /// Beacon kept it: their hash is computed from <see cref="StepsJson"/> and <see cref="FinalQuery"/> when needed.
    /// </summary>
    public string? ContentHash { get; set; }

    // Navigation
    public Query Query { get; set; } = null!;
}
