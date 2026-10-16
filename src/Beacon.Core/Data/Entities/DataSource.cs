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

    /// <summary>
    /// Set for data sources the host application declares with <c>ExposeDbContext&lt;TContext&gt;</c>. Such a source
    /// is owned by the host: its connection data is only an encrypted reference to a host connection-string name,
    /// its metadata comes from the host's EF model, and it cannot be edited or deleted through the UI/REST API.
    /// Null for every ordinary data source.
    /// </summary>
    public string? HostManagedKey { get; set; }

    /// <summary>Hash of the exposed host model slice at the last sync; an unchanged hash skips the metadata rewrite.</summary>
    public string? HostModelHash { get; set; }

    /// <summary>
    /// Whether the source, while no project links it, stays reachable by every reader (as before projects bounded
    /// access). True for sources that existed when project access was introduced; false for every source created since,
    /// which only an Admin reaches until it is linked to a project. A path that unlinks a source's last project must
    /// clear it first.
    /// </summary>
    public bool OpenWhenUnlinked { get; set; }

    /// <summary>
    /// An Admin's mark that the data source is a destination for data migrations, and nothing else
    /// (<see cref="Services.MigrationWriteTargets"/>). Its connection may carry a login with write grants, so Beacon never
    /// reads through it: query steps, previews, subscriptions, data quality, MCP tools, AI grounding, documentation and
    /// metadata scans refuse it or leave it out, and only the migration writer connects with its credentials. Off for
    /// every data source until an Admin turns it on, which the deployment must allow (<c>Beacon:Migrations:Enabled</c>).
    /// </summary>
    public bool AllowMigrationWrites { get; set; }

    /// <summary>
    /// When a DataSourceAdmin disabled the source; null while it is enabled. A disabled source makes no connection at all:
    /// the connection resolver refuses it before anything is decrypted, so every query, preview, subscription, data-quality
    /// check, MCP tool, AI path and metadata scan that reads it fails, and the project listings MCP and AI read leave it out.
    /// </summary>
    public DateTime? DisabledTime { get; set; }

    /// <summary>The <c>Users.Id</c> of who disabled the source (no foreign key); null while it is enabled.</summary>
    public int? DisabledByUserId { get; set; }

    public List<QueryStep> QuerySteps { get; set; } = new();
}
