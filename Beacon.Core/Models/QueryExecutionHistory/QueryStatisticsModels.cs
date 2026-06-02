namespace Beacon.Core.Models.QueryExecutionHistory;

public class NotificationStatisticsData
{
    public List<NotificationDateStatisticsData> NotificationDateStatistics { get; set; }
}

public class NotificationDateStatisticsData
{
    public DateTime Date { get; set; }

    public int TotalQueries { get; set; }

    public int NotificationsSent { get; set; }

    public int MigrationExecutions { get; set; }

    public int SuccessfulMigrationExecutions { get; set; }
}

public class DashboardStatisticsData
{
    public int TotalSubscriptions { get; set; }

    public int TotalQueries { get; set; }

    public int TotalQueriesExecuted { get; set; }

    public int TotalNotificationsSent { get; set; }

    public int ActiveSubscriptions { get; set; }

    public int TotalMigrationJobs { get; set; }

    public int EnabledMigrationJobs { get; set; }

    public int TotalMigrationExecutions { get; set; }

    public int SuccessfulMigrationExecutions { get; set; }

    // Task Statistics
    public int TotalTasks { get; set; }

    public int UnresolvedTasks { get; set; }

    public int ResolvedTasks { get; set; }

    // Anomaly Statistics
    public int TotalAnomaliesDetected { get; set; }

    public int ActiveAnomalyConfigs { get; set; }

    public int AnomaliesLast24Hours { get; set; }

    // Notification Channel Breakdown
    public Dictionary<string, int> NotificationsByChannel { get; set; } = new();

    // Recent Activity
    public List<RecentActivityItem> RecentActivities { get; set; } = new();

    // Top Subscriptions
    public List<TopSubscriptionItem> TopSubscriptions { get; set; } = new();

    // Data Sources
    public int TotalDataSources { get; set; }

    public int TotalRecipients { get; set; }

    // Execution Time Statistics
    public double AvgExecutionTimeMs { get; set; }

    public double MinExecutionTimeMs { get; set; }

    public double MaxExecutionTimeMs { get; set; }

    public List<ExecutionTimeDataPoint> ExecutionTimeHistory { get; set; } = new();
}

public class ExecutionTimeDataPoint
{
    public DateTime Date { get; set; }

    public double AvgExecutionTimeMs { get; set; }

    public double MinExecutionTimeMs { get; set; }

    public double MaxExecutionTimeMs { get; set; }
}

public class RecentActivityItem
{
    public string Type { get; set; } = null!;

    public string Description { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public string? Icon { get; set; }

    public string? Link { get; set; }

    public string? Status { get; set; }
}

public class TopSubscriptionItem
{
    public int SubscriptionId { get; set; }

    public string SubscriptionName { get; set; } = null!;

    public int ExecutionCount { get; set; }

    public int NotificationCount { get; set; }

    public DateTime? LastExecuted { get; set; }
}