using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class QueryTask : ArchivableBaseEntity
{
    public required int SubscriptionId { get; set; }
    public required int LatestResultCount { get; set; }
    public DateTime? LastNotificationAt { get; set; }
    public bool Resolved { get; set; } = false;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }

    // Navigation properties
    public List<Notification> Notifications { get; set; } = new();
    public Subscription Subscription { get; set; } = null!;
}
