using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class Query : ArchivableBaseEntity
{
    public required string SqlValue { get; set; }

    public required int ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public List<Subscription> Subscriptions { get; set; } = new();

    public List<QueryParameter> Parameters { get; set; } = new();
}
