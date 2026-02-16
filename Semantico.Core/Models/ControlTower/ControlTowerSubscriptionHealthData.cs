using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.ControlTower;

public record ControlTowerSubscriptionHealthData
{
    public int SubscriptionId { get; init; }
    public string QueryName { get; init; } = null!;
    public string? DataSourceName { get; init; }
    public string? FolderPath { get; init; }

    // RAG Status Calculation
    public HealthStatus HealthStatus { get; init; }
    public int TotalExecutions { get; init; }
    public int SuccessfulExecutions { get; init; }
    public int FailedExecutions { get; init; }
    public double SuccessRate { get; init; }

    // Last Execution Info
    public DateTime? LastExecutionTime { get; init; }
    public NotificationStatus? LastExecutionStatus { get; init; }
    public int? LastResultCount { get; init; }

    // Task Metrics
    public int UnresolvedTaskCount { get; init; }
    public int TotalTaskCount { get; init; }

    // Anomaly Metrics
    public int AnomalyCount30Days { get; init; }
    public List<AnomalySparklinePoint> AnomalySparkline { get; init; } = new();

    // Configuration
    public bool IsActive { get; init; }
    public bool CreateTasks { get; init; }
    public bool StoreResults { get; init; }
    public bool HasAnomalyDetection { get; init; }

    // AI Actor
    public int? AiActorId { get; init; }
    public string? AiActorName { get; init; }
}
