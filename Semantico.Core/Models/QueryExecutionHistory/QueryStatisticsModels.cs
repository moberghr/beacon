namespace Semantico.Core.Models.QueryExecutionHistory;

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
}