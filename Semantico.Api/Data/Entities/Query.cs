using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class Query : BaseEntity
{
    public required string SqlValue { get; set; }

    public required string CronExpression { get; set; }

    public required int ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public List<Notification> Notifications { get; set; } = new();
}
