using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.RowSecurity;

/// <summary>
/// An attribute users hold values of (a branch, an advisor id), which row policies compare a table's column with. A user
/// with no value of an attribute a policy's clause names gets no row through that policy. Values read through their
/// attribute leave out an archived attribute's (its soft-delete filter hides it), so an archived attribute grants no row.
/// </summary>
public class AttributeDefinition : ArchivableBaseEntity, IAttributedEntity
{
    /// <summary>The name policies refer to the attribute by: unique among live attributes.</summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    public string? Description { get; set; }

    public required RowAttributeValueType ValueType { get; set; }

    /// <inheritdoc />
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc />
    public string? ModifiedByUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedTime { get; set; }

    public List<UserAttributeValue> Values { get; set; } = new();
}
