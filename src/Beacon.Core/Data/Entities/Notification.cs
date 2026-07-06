using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class Notification : BaseEntity
{
    public int QueryExecutionHistoryId { get; set; }

    public required int RecipientId { get; set; }

    public required NotificationType Type { get; set; }

    public required DateTime SentAt { get; set; }

    public string? Results { get; set; }

    public int? TaskId { get; set; }

    public QueryExecutionHistory QueryExecutionHistory { get; set; } = null!;

    public Recipient Recipient { get; set; } = null!;

    public QueryTask? Task { get; set; }
}