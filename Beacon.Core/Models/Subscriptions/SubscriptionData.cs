using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Anomaly;
using Beacon.Core.Models.Recipients;

namespace Beacon.Core.Models.Subscriptions;

public class SubscriptionData
{
    public int? SubscriptionId { get; set; }

    public int QueryId { get; set; }

    public string QueryName { get; set; }

    public string CronExpression { get; set; }
    
    public int? MaxRows { get; set; }

    /// <summary>
    /// Minimum row count threshold for sending notifications.
    /// If set, notifications will only be sent if the query result count is greater than or equal to this value.
    /// </summary>
    public int? MinimumRowCount { get; set; }

    public bool IncludeAttachment { get; set; } = true;

    public FileType? ResultAttachmentType { get; set; }

    public bool ShowQuery { get; set; } = true;
    
    /// <summary>
    /// Query execution timeout in seconds. If null, no timeout is applied.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
    
    /// <summary>
    /// When true, query results will be stored in the notification record.
    /// </summary>
    public bool StoreResults { get; set; } = false;

    /// <summary>
    /// When true, a task will be created/updated for this subscription on each execution.
    /// </summary>
    public bool CreateTasks { get; set; } = false;

    /// <summary>
    /// Controls when notifications should be sent for this subscription.
    /// Default is OnResultCountChange (send when result count differs from last execution).
    /// </summary>
    public NotificationTrigger NotificationTrigger { get; set; } = NotificationTrigger.OnResultCountChange;

    public List<RecipientData> Recipients { get; set; } = new();

    public List<SubscriptionParameterData> Parameters { get; set; } = new();

    /// <summary>
    /// Anomaly detection configuration for this subscription
    /// </summary>
    public AnomalyConfigData? AnomalyConfig { get; set; }

    /// <summary>
    /// AI Actor ID if this subscription is managed by an AI Actor, null if user-created
    /// </summary>
    public int? AiActorId { get; set; }

    /// <summary>
    /// Name of the AI Actor managing this subscription
    /// </summary>
    public string? AiActorName { get; set; }
}