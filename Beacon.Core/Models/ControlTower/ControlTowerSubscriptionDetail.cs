using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.ControlTower;

public record ControlTowerSubscriptionDetail
{
    public int SubscriptionId { get; init; }
    public string QueryName { get; init; } = null!;
    public int QueryId { get; init; }
    public string? FolderPath { get; init; }
    public string CronExpression { get; init; } = null!;
    public int TimeRangeDays { get; init; }
    public List<ControlTowerExecutionItem> RecentExecutions { get; init; } = new();
    public List<ControlTowerOpenTask> OpenTasks { get; init; } = new();
    public List<ControlTowerAnomaly> RecentAnomalies { get; init; } = new();
}

public record ControlTowerExecutionItem
{
    public int ExecutionId { get; init; }
    public DateTime CreatedTime { get; init; }
    public NotificationStatus NotificationStatus { get; init; }
    public int ResultCount { get; init; }
    public double ExecutionTimeMs { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ControlTowerOpenTask
{
    public int TaskId { get; init; }
    public DateTime CreatedTime { get; init; }
    public DateTime? SnoozedUntil { get; init; }
    public int LatestResultCount { get; init; }
    public TaskPriority Priority { get; init; }
    public string? AssigneeUserId { get; init; }
}

public record ControlTowerAnomaly
{
    public int AnomalyId { get; init; }
    public DateTime DetectedTime { get; init; }
    public string Severity { get; init; } = "Medium";
    public decimal CurrentValue { get; init; }
    public string? Explanation { get; init; }
    public bool Acknowledged { get; init; }
}
