namespace Beacon.Core.Models.DataQuality;

public record DataQualityOverviewData
{
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public double AverageScore { get; init; }
    public int TotalTables { get; init; }
    public int HealthyTables { get; init; }
    public int DegradingTables { get; init; }
    public int ActiveContracts { get; init; }
    public List<DataQualityScoreData> TableScores { get; init; } = new();
}
