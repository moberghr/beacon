using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class Notification : BaseEntity
{
    public required string Value { get; set; }

    public required int QueryId { get; set; }

    public required NotificationType NotificationType { get; set; }

    public Query Query { get; set; } = null!;
}

public enum NotificationType
{
    Teams = 1,
    Email = 2
}
