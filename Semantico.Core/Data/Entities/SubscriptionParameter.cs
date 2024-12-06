using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

internal class SubscriptionParameter : ArchivableBaseEntity
{
    public int SubscriptionId { get; set; }

    public Subscription Subscription { get; set; } = null!;
    
    public required string QueryPlaceholder { get; set; }

    public required string Value { get; set; }
}
