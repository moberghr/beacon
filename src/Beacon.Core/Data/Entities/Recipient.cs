using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class Recipient : ArchivableBaseEntity, IAttributedEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Destination { get; set; }

    public NotificationType NotificationType { get; set; }

    public string? HeadersJson { get; set; }

    public string? BodyTemplate { get; set; }

    /// <summary>
    /// While the change approval workflow governs the recipient: the hash of where it delivers (type, destination and
    /// headers, <see cref="ChangeApprovals.RecipientDestinationDefinition"/>) a second person approved, and deliveries
    /// reach it only while it hashes to it; <see cref="ChangeApprovals.ChangeApprovalHashes.NothingApproved"/> for a
    /// recipient created under the workflow and not approved yet. Null for a recipient the workflow does not govern
    /// (stored before Beacon reviewed recipients, or last saved with the workflow off), which receives deliveries as
    /// before.
    /// </summary>
    public string? ApprovedDestinationHash { get; set; }

    /// <inheritdoc />
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc />
    public string? ModifiedByUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedTime { get; set; }

    public List<Subscription> Subscriptions { get; set; } = new();

    public List<DataContract> DataContracts { get; set; } = new();

    public List<Notification> Notifications { get; set; } = new();
}
