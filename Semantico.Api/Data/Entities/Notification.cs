using Semantico.Api.Data.Entities.Base;
using Semantico.Api.Data.Enums;

namespace Semantico.Api.Data.Entities;

public class Notification : BaseEntity
{
    public required string Recipient { get; set; }

    public required NotificationType NotificationType { get; set; }

    public required int SubscriptionId { get; set; }

    public Subscription Subscription { get; set; } = null!;

    public required int ResultCount { get; set; }
}
