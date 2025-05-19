using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

internal class QueryExecutionHistory : BaseEntity
{
    public required int SubscriptionId { get; set; }

    public required int ResultCount { get; set; }

    public required string CompiledSql { get; set; }

    public required NotificationStatus NotificationStatus { get; set; }
    
    public required double ExecutionTimeMs { get; set; }

    public Subscription Subscription { get; set; } = null!;
}
