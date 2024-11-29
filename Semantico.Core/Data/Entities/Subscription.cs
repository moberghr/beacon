using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

internal class Subscription : ArchivableBaseEntity
{
    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }

    public Query Query { get; set; } = null!;

    public List<Recipient> Recipients { get; set; } = new();

    public List<SubscriptionParameter> Parameters { get; set; } = new();

    public List<QueryExecutionHistory> QueryExecutionHistory { get; set; } = new();
}
