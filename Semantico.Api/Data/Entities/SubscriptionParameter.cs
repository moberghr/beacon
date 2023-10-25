using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class SubscriptionParameter : ArchivableBaseEntity
{
    public required int SubscriptionId { get; set; }

    public Subscription Subscription { get; set; } = null!;

    public required string QueryPlaceholder { get; set; }

    public required string Value { get; set; }
}
