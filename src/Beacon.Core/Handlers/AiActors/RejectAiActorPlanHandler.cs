using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.AiActors;

public record RejectAiActorPlanCommand : IRequest<RejectAiActorPlanResult>
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public required string Reason { get; init; }
}

public record RejectAiActorPlanResult
{
    public bool Success { get; init; }
    public int PlanId { get; init; }
}
