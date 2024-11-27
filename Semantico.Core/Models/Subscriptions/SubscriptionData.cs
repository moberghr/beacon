using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Subscriptions;

public class SubscriptionData
{
    public int? SubscriptionId { get; set; }

    public string Name { get; set; }

    public int QueryId { get; set; }

    public string CronExpression { get; set; }

    public NotificationType NotificationType { get; set; }

    public string Recipient { get; set; }

    public List<SubscriptionParamaterData> Parameters { get; set; } = new();
}