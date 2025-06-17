using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

internal class Notification : BaseEntity
{
    public int QueryExecutionHistoryId { get; set; }
    
    public required int RecipientId { get; set; }
    
    public required NotificationType Type { get; set; }
    
    public required DateTime SentAt { get; set; }
    
    public QueryExecutionHistory QueryExecutionHistory { get; set; } = null!;
    
    public Recipient Recipient { get; set; } = null!;
}