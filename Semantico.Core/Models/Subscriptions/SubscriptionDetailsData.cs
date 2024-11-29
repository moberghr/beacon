using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Models.Subscriptions;

public class SubscriptionDetailsData
{
    public int SubscriptionId { get; set; }

    public int QueryId { get; set; }
    
    public string QueryName { get; set; }

    public string Status { get; set; }
    
    public string CronExpression { get; set; }

    public List<SubscriptionParamaterData> Parameters { get; set; } = new();

    public List<RecipientData> Recipients { get; set; } = new();
}