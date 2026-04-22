using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.QueryExecutionHistory;

public class NotificationData
{
    public int Id { get; set; }
    
    public DateTime Created { get; set; }
    
    public string RecipientName { get; set; }
    
    public NotificationType NotificationType { get; set; }
    
    public DateTime SentAt { get; set; }
}