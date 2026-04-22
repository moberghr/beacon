using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class SubscriptionParameter : ArchivableBaseEntity
{
    public int SubscriptionId { get; set; }

    public Subscription Subscription { get; set; } = null!;

    public required string QueryPlaceholder { get; set; }

    public required string Value { get; set; }
}
