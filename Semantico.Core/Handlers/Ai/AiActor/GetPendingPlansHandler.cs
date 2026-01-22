using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record GetPendingPlansQuery : IRequest<GetPendingPlansResult>
{
    public required int ActorId { get; init; }
}

public record GetPendingPlansResult
{
    public List<PendingPlanSummary> Plans { get; init; } = [];
}
