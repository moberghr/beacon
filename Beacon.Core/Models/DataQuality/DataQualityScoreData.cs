using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataQuality;

public record DataQualityScoreData
{
    public int Id { get; init; }
    public int DataSourceId { get; init; }
    public string SchemaName { get; init; } = null!;
    public string TableName { get; init; } = null!;
    public double Score { get; init; }
    public DateTime EvaluatedAt { get; init; }
    public DataQualityTrendDirection TrendDirection { get; init; }
    public double? PreviousScore { get; init; }
}
