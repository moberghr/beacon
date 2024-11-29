using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Models.Subscriptions;

public class SubscriptionData
{
    public int? SubscriptionId { get; set; }

    public int QueryId { get; set; }

    public string QueryName { get; set; }

    public string CronExpression { get; set; }

    public List<RecipientData> Recipients { get; set; } = new();

    public List<SubscriptionParamaterData> Parameters { get; set; } = new();
}