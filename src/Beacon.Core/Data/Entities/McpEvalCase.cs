using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// A golden eval case for the MCP text-to-SQL harness: a question + its gold SQL and a frozen
/// expected result-set fingerprint. Promoted from an <see cref="McpQuerySignal"/> (tracked via
/// <see cref="SourceSignalId"/>). Follows the plain-int-FK convention of the other Mcp learning
/// entities — no navigation properties.
/// </summary>
public class McpEvalCase : BaseEntity
{
    public int ProjectId { get; set; }
    public int DataSourceId { get; set; }

    public required string Question { get; set; }
    public required string GoldSql { get; set; }

    /// <summary>Normalized expected result set (ordered column list + row hashes). Stored, not re-run.</summary>
    public string? GoldResultFingerprint { get; set; }

    /// <summary>The <see cref="McpQuerySignal"/> this case was promoted from, if any.</summary>
    public int? SourceSignalId { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    /// <summary>The <c>Users.Id</c> of the user who promoted the case; null for a case created before Beacon recorded it.</summary>
    public int? PromotedByUserId { get; set; }

    /// <summary>The <c>Users.Id</c> of the user who last edited the case; null until it is edited.</summary>
    public int? ModifiedByUserId { get; set; }
}
