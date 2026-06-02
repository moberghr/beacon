using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.DataQuality;

public class DataQualityScore : BaseEntity
{
    public int DataSourceId { get; set; }

    public required string SchemaName { get; set; }

    public required string TableName { get; set; }

    public double Score { get; set; }

    public DateTime EvaluatedAt { get; set; }

    public DataQualityTrendDirection TrendDirection { get; set; } = DataQualityTrendDirection.Stable;

    public double? PreviousScore { get; set; }

    public DataSource DataSource { get; set; } = null!;
}
