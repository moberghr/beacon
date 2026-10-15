using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

public record RefineAiActorCommand : IRequest<RefineAiActorResult>, IProjectAuthorizedRequest
{
    public required int ActorId { get; init; }
    public required string Feedback { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActor(ActorId), ProjectAction.Query)
    ];
}

public record RefineAiActorResult
{
    public bool Success { get; init; }
    public int ExecutionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public string? DecisionSummary { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }
}
