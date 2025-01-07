using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

internal class Recipient : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Destination { get; set; }

    public FileType? ResultAttachment { get; set; }

    public NotificationType NotificationType { get; set; }

    public List<Subscription> Subscriptions { get; set; } = new();
}
