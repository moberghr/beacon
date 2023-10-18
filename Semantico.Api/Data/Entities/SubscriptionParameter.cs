using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class SubscriptionParameter : BaseEntity
{
    public required int SubscriptionId { get; set; }

    public Subscription Subscription { get; set; } = null!;

    public required string Name { get; set; }

    public required string Value { get; set; }
}
