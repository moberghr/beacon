using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Ai.AiActor;

public record GetPendingPlansQuery : IRequest<GetPendingPlansResult>
{
    public required int ActorId { get; init; }
}

public record GetPendingPlansResult
{
    public List<PendingPlanSummary> Plans { get; init; } = [];
}
