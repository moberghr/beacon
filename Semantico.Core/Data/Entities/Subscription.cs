using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class Subscription : ArchivableBaseEntity
{
    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }
    
    public int? MaxRows { get; set; }

    /// <summary>
    /// Minimum row count threshold for sending notifications.
    /// If set, notifications will only be sent if the query result count is greater than or equal to this value.
    /// This filter is applied independently of the NotificationTrigger setting.
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

    /// <summary>
    /// If this subscription was created by an AI Actor, the actor's ID.
    /// Null means user-created.
    /// </summary>
    public int? AiActorId { get; set; }

    public Query Query { get; set; } = null!;

    /// <summary>
    /// The AI Actor that created this subscription (null if user-created)
    /// </summary>
    public AiActor? AiActor { get; set; }

    public List<Recipient> Recipients { get; set; } = new();

    public List<SubscriptionParameter>? Parameters { get; set; } = new();

    public List<QueryExecutionHistory>? QueryExecutionHistory { get; set; } = new();
}
