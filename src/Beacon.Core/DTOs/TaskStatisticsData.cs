namespace Beacon.Core.DTOs;

public record TaskStatisticsData
{
    public required int TotalTasks { get; init; }
    public required int UnresolvedCount { get; init; }
    public required int ResolvedCount { get; init; }
    public double? AverageResolutionTimeHours { get; init; }
}
