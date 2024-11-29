using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Subscriptions;

public class SubscriptionData
{
    public int? SubscriptionId { get; set; }

    public int QueryId { get; set; }

    public string CronExpression { get; set; }

    public int RecipientId { get; set; }

    public string RecipientName { get; set; }

    public List<SubscriptionParamaterData> Parameters { get; set; } = new();
}