using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

internal class Subscription : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }

    public Query Query { get; set; } = null!;

    public required string Recipient { get; set; }

    public NotificationType NotificationType { get; set; }

    public List<SubscriptionParameter> Parameters { get; set; } = new();

    public List<QueryExecutionHistory> QueryExecutionHistory { get; set; } = new();
}
