using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// One table or column description Beacon wrote into a data source's catalog: who wrote it, when, from which patch, and
/// whether the table changed since. The description text stays in the catalog (<c>DatabaseMetadata.TableDescription</c>,
/// <c>ColumnMetadata.Description</c>); the row is keyed by names, not by the metadata row id, so it survives a metadata
/// refresh. A description the catalog brought (never written by Beacon) has no row. No foreign keys: names, ids, a hash and
/// a model name only.
/// </summary>
public class McpDescriptionItem : BaseEntity
{
    public int DataSourceId { get; set; }

    public string SchemaName { get; set; } = null!;

    public string TableName { get; set; } = null!;

    /// <summary>The column the description belongs to; an empty string for the table's own description.</summary>
    public string ColumnName { get; set; } = string.Empty;

    public McpDescriptionSource Source { get; set; }

    public DateTime WrittenAt { get; set; }

    /// <summary>The <c>Users.Id</c> of the person who applied the patch.</summary>
    public int? WrittenByUserId { get; set; }

    /// <summary>The model that proposed a <see cref="McpDescriptionSource.Generated"/> description; null otherwise.</summary>
    public string? GeneratedByModel { get; set; }

    /// <summary>The documentation patch the description was applied from; no foreign key.</summary>
    public int? SourcePatchId { get; set; }

    /// <summary>SHA-256 hex of the table's column names and data types in ordinal order when the description was written.</summary>
    public string StructureHash { get; set; } = null!;

    /// <summary>When a refresh first found the table or column gone, or the table's structure changed; null while current.</summary>
    public DateTime? StaleSince { get; set; }
}
