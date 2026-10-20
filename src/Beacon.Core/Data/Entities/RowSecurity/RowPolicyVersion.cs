using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.RowSecurity;

/// <summary>
/// One definition of a row policy, never changed once recorded (only its <see cref="Status"/> moves): a change of the
/// policy records a new version.
/// </summary>
public class RowPolicyVersion : BaseEntity
{
    public int RowPolicyId { get; set; }

    /// <summary>1 for the policy's first version, then one more for each version after it.</summary>
    public int VersionNumber { get; set; }

    public RowPolicyVersionStatus Status { get; set; }

    /// <summary>
    /// The clauses a row must all meet, as JSON: each compares a column of the table with the values the reading user holds
    /// of an attribute (<c>[{"column": "branch_id", "operator": "in", "attribute": "branch"}]</c>).
    /// </summary>
    public required string DefinitionJson { get; set; }

    /// <summary>The SHA-256 (lowercase hex) of the version's definition on its table, as its approval request names it.</summary>
    public required string ContentHash { get; set; }

    /// <summary>Who submitted the version: the caller's <c>Users.ExternalId</c>.</summary>
    public string? CreatedByUserId { get; set; }

    public RowPolicy RowPolicy { get; set; } = null!;
}
