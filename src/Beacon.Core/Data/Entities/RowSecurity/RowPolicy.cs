using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.RowSecurity;

/// <summary>
/// A row policy on one table of a data source, in every project that reaches the source. A Protected table is read only
/// through its policies: a row is readable when any of its policies' active versions lets it through, and a policy without
/// an active version lets nothing through. A change is a new version, enforced only once a second person approves it;
/// the active version stays in force until then. The data source and table never change.
/// </summary>
public class RowPolicy : ArchivableBaseEntity, IAttributedEntity
{
    public int DataSourceId { get; set; }

    public required string SchemaName { get; set; }

    public required string TableName { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The approved version the policy enforces; null until a version is first approved.</summary>
    public int? ActiveVersionId { get; set; }

    /// <inheritdoc />
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc />
    public string? ModifiedByUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedTime { get; set; }

    public DataSource DataSource { get; set; } = null!;

    public RowPolicyVersion? ActiveVersion { get; set; }

    public List<RowPolicyVersion> Versions { get; set; } = new();
}
