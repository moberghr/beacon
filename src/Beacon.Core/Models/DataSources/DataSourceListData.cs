using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Models.DataSources;

public class DataSourceListData
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required DataSourceType DataSourceType { get; init; }

    public DatabaseEngineType? DatabaseEngineType { get; init; }

    public bool MetadataLoadingEnabled { get; init; }

    public List<QueryData> Queries { get; init; } = new();

    public int MigrationJobsCount { get; init; }

    public DateTime? ArchivedTime { get; init; }
}
