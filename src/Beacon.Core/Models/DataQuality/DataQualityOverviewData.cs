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

    /// <summary>
    /// The enabled contracts on the data source whose last scheduled evaluation was skipped because their owner may no
    /// longer run them (none has run since).
    /// </summary>
    public int OwnerNotAuthorizedContracts { get; init; }

    public List<DataQualityScoreData> TableScores { get; init; } = new();
}
