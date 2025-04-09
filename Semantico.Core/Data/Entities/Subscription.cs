using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

internal class Subscription : ArchivableBaseEntity
{
    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }
    
    public int? MaxRows { get; set; }
    
    public bool IncludeAttachment { get; set; } = true;
    
    public bool ShowQuery { get; set; } = true;

    public Query Query { get; set; } = null!;

    public List<Recipient> Recipients { get; set; } = new();

    public List<SubscriptionParameter> Parameters { get; set; } = new();

    public List<QueryExecutionHistory> QueryExecutionHistory { get; set; } = new();
}
