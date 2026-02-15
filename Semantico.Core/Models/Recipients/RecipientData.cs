using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Recipients;

public class RecipientData
{
    public int? RecipientId { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }

    public string Destination { get; set; }

    public NotificationType NotificationType { get; set; }

    public string? HeadersJson { get; set; }

    public string? BodyTemplate { get; set; }
}
