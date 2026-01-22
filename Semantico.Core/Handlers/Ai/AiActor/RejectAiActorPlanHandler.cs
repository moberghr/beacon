using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

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
