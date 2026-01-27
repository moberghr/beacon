using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.DataSources;

public class DataSourceData
{
    public int? DataSourceId { get; init; }

    public string Name { get; set; }

    public DataSourceType DataSourceType { get; set; } = DataSourceType.Database;

    public string ConnectionString { get; set; }

    public DatabaseEngineType? DatabaseEngineType { get; set; }
}
