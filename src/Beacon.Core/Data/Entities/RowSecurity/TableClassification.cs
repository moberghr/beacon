using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.RowSecurity;

/// <summary>
/// The row-security class of one table of a data source. A data source with a Protected table refuses its tables without
/// a class in force (a table with no row here has none). Removed when the table's class is taken away: the security audit
/// trail records the change.
/// </summary>
public class TableClassification : BaseEntity, IAttributedEntity
{
    public int DataSourceId { get; set; }

    public required string SchemaName { get; set; }

    public required string TableName { get; set; }

    /// <summary>The class in force; null while the table's first class waits for approval.</summary>
    public TableClass? Class { get; set; }

    /// <summary>A class waiting for a second person's approval; null when none waits.</summary>
    public TableClass? RequestedClass { get; set; }

    /// <inheritdoc />
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc />
    public string? ModifiedByUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedTime { get; set; }

    public DataSource DataSource { get; set; } = null!;
}
