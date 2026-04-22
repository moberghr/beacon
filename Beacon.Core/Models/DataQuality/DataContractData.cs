using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataQuality;

public record DataContractData
{
    public int Id { get; init; }
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public string SchemaName { get; init; } = null!;
    public string TableName { get; init; } = null!;
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public string CronExpression { get; init; } = null!;
    public bool IsEnabled { get; init; }
    public string? OwnerUserId { get; init; }
    public bool AlertOnFailure { get; init; }
    public int FailureThresholdScore { get; init; }
    public DateTime CreatedTime { get; init; }
    public double? LatestScore { get; init; }
    public List<DataContractRuleData> Rules { get; init; } = new();
    public List<DataContractRecipientData> Recipients { get; init; } = new();
}

public record DataContractRecipientData
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string Destination { get; init; } = null!;
    public NotificationType NotificationType { get; init; }
}
