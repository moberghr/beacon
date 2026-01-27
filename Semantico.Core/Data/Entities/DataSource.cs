using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

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

    public List<QueryStep> QuerySteps { get; set; } = new();
}
