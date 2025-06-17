using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.QueryExecutionHistory;

public class NotificationData
{
    public DateTime Created { get; set; }
    
    public string RecipientName { get; set; }
    
    public NotificationType NotificationType { get; set; }
    
    public DateTime SentAt { get; set; }
}