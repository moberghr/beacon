using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class Subscription : BaseEntity
{
    public required string Name { get; set; }

    public required int QueryId { get; set; }

    public required string CronExpression { get; set; }

    public Query Query { get; set; } = null!;
}
