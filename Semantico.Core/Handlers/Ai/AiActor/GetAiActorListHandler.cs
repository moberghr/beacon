using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record GetAiActorListQuery : IRequest<GetAiActorListResult>
{
    public required int DataSourceId { get; init; }
    public bool? IncludeArchived { get; init; }
}

public record GetAiActorListResult
{
    public List<AiActorListItem> Actors { get; init; } = new();
}

public record AiActorListItem
{
    public int ActorId { get; init; }
    public string Name { get; init; } = null!;
    public string Instructions { get; init; } = null!;
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public AiActorStatus Status { get; init; }
    public int ThinkCount { get; init; }
    public DateTime? LastThinkTime { get; init; }
    public decimal TotalCost { get; init; }
    public DateTime CreatedTime { get; init; }
}
