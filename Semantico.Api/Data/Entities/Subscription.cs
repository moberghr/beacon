using Semantico.Api.Data.Entities.Base;
using Semantico.Api.Data.Enums;

namespace Semantico.Api.Data.Entities;

public class Subscription : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }

    public Query Query { get; set; } = null!;

    public required string Recipient { get; set; }
    
    public NotificationType NotificationType { get; set; }

    public List<SubscriptionParameter> Parameters { get; set; } = new();
}
