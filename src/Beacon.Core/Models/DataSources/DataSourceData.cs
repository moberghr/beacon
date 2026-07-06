using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataSources;

public class DataSourceData
{
    public int? DataSourceId { get; init; }

    public string Name { get; set; }

    public DataSourceType DataSourceType { get; set; } = DataSourceType.Database;

    public string ConnectionString { get; set; }

    public DatabaseEngineType? DatabaseEngineType { get; set; }

    // Metadata loading options (Database type only)
    public bool MetadataLoadingEnabled { get; set; } = true;
    public int MetadataMaxTables { get; set; }
    public int MetadataMaxColumnsPerTable { get; set; }
    public bool MetadataLoadTableNamesOnly { get; set; }
    public List<string> MetadataExcludeSchemas { get; set; } = new();
    public List<string> MetadataIncludeSchemas { get; set; } = new();
}
