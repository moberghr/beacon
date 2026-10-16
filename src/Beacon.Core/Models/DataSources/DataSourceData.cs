using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataSources;

public class DataSourceData
{
    public int? DataSourceId { get; init; }

    public string Name { get; set; }

    public DataSourceType DataSourceType { get; set; } = DataSourceType.Database;

    /// <summary>
    /// The connection data. On an update, null or empty keeps the stored connection (it is never shown, so it is never sent
    /// back); on a create it is required.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// On an update, the caller confirmed that the new connection points somewhere else (another engine, host, port or
    /// database, or a target Beacon cannot read from the connection data). Without it such a change is refused.
    /// </summary>
    public bool ConfirmTargetChange { get; init; }

    public DatabaseEngineType? DatabaseEngineType { get; set; }

    // Metadata loading options (Database type only). On an update, null keeps the stored value (an empty schema list clears
    // it); on a create, null is the default: loading on, no limits, all schemas.
    public bool? MetadataLoadingEnabled { get; set; }
    public int? MetadataMaxTables { get; set; }
    public int? MetadataMaxColumnsPerTable { get; set; }
    public bool? MetadataLoadTableNamesOnly { get; set; }
    public List<string>? MetadataExcludeSchemas { get; set; }
    public List<string>? MetadataIncludeSchemas { get; set; }

    /// <summary>The projects a new data source is linked into when it is created (checked by the caller).</summary>
    public List<int> ProjectIds { get; set; } = new();

    /// <summary>The <c>Users.Id</c> of the user who links the new data source into <see cref="ProjectIds"/>, recorded on each link.</summary>
    public int? LinkedByUserId { get; set; }
}
