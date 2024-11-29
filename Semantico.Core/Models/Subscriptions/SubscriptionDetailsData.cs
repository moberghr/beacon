using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Subscriptions;

public class SubscriptionDetailsData
{
    public int SubscriptionId { get; set; }

    public int QueryId { get; set; }
    
    public string QueryName { get; set; }

    public string Status { get; set; }
    
    public string RecipientName { get; set; }

    public string RecipientDestination { get; set; }

    public NotificationType NotificationType { get; set; }
    
    public string CronExpression { get; set; }
    
    public List<SubscriptionParamaterData> Parameters { get; set; }
}

public class NotificationData
{
    public DateTime CreatedTime { get; set; }
    
    public NotificationType NotificationType { get; set; }

    public string Recipient { get; set; }

    public int ResultCount { get; set; }
}