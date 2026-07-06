using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class Recipient : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Destination { get; set; }

    public NotificationType NotificationType { get; set; }

    public string? HeadersJson { get; set; }

    public string? BodyTemplate { get; set; }

    public List<Subscription> Subscriptions { get; set; } = new();

    public List<DataContract> DataContracts { get; set; } = new();

    public List<Notification> Notifications { get; set; } = new();
}
