using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class DataSource : ArchivableBaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Type of data source (Database, CloudWatch, etc.)
    /// </summary>
    public required DataSourceType DataSourceType { get; set; }

    /// <summary>
    /// Encrypted connection data
    /// - For Database types: connection string
    /// - For CloudWatch/other providers: JSON configuration
    /// </summary>
    public required string EncryptedConnectionData { get; set; }

    /// <summary>
    /// Only applicable for Database type data sources
    /// </summary>
    public DatabaseEngineType? DatabaseEngineType { get; set; }

    // Metadata loading options (only applicable for Database type)
    public bool MetadataLoadingEnabled { get; set; } = true;
    public int MetadataMaxTables { get; set; }
    public int MetadataMaxColumnsPerTable { get; set; }
    public bool MetadataLoadTableNamesOnly { get; set; }
    public string? MetadataExcludeSchemas { get; set; }
    public string? MetadataIncludeSchemas { get; set; }

    /// <summary>
    /// Admin assertion that the connection's login holds no write permission (Wave 1.2 verifies it at readiness
    /// and the deployment lock requires it for every data source in scope).
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Append <c>ApplicationIntent=ReadOnly</c> for availability-group listeners (routes to a secondary; a no-op
    /// elsewhere — never a write blocker). Consumed from Wave 1.2.
    /// </summary>
    public bool UseReadOnlyIntent { get; set; }

    public List<QueryStep> QuerySteps { get; set; } = new();
}
