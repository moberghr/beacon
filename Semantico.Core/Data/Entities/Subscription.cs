using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

internal class Subscription : ArchivableBaseEntity
{
    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }
    
    public int? MaxRows { get; set; }
    
    public bool IncludeAttachment { get; set; } = true;
    
    public bool ShowQuery { get; set; } = true;
    
    /// <summary>
    /// Query execution timeout in seconds. If null, no timeout is applied.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
    
    /// <summary>
    /// When true, query results will be stored in the notification record.
    /// </summary>
    public bool StoreResults { get; set; } = false;

    public Query Query { get; set; } = null!;

    public List<Recipient> Recipients { get; set; } = new();

    public List<SubscriptionParameter>? Parameters { get; set; } = new();

    public List<QueryExecutionHistory>? QueryExecutionHistory { get; set; } = new();
}
