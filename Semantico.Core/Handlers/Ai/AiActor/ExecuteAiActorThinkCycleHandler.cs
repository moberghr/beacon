using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class ExecuteAiActorThinkCycleHandler : IRequestHandler<ExecuteAiActorThinkCycleCommand, ExecuteAiActorThinkCycleResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<ExecuteAiActorThinkCycleHandler> _logger;

    public ExecuteAiActorThinkCycleHandler(
        IAiActorService aiActorService,
        ILogger<ExecuteAiActorThinkCycleHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<ExecuteAiActorThinkCycleResult> Handle(
        ExecuteAiActorThinkCycleCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manually triggering think cycle for AI Actor {ActorId}", request.ActorId);

        var result = await _aiActorService.ExecuteThinkCycleAsync(
            request.ActorId,
            null,
            cancellationToken);

        return new ExecuteAiActorThinkCycleResult
        {
            Success = result.Success,
            ExecutionId = result.ExecutionId,
            Phase = result.Phase,
            DecisionSummary = result.DecisionSummary,
            Findings = result.Findings,
            QueriesAnalyzed = result.QueriesAnalyzed,
            QueriesCreated = result.QueriesCreated,
            QueriesRefined = result.QueriesRefined,
            SubscriptionsCreated = result.SubscriptionsCreated,
            NotificationsTriggered = result.NotificationsTriggered,
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            Duration = result.Duration,
            ErrorMessage = result.ErrorMessage,
            Actions = result.Actions.Select(a => new ExecutedActionInfo
            {
                ActionType = a.ActionType,
                Reasoning = a.Reasoning,
                Success = a.Success,
                ErrorMessage = a.ErrorMessage,
                ResultEntityId = a.ResultEntityId
            }).ToList()
        };
    }
}

public record ExecuteAiActorThinkCycleCommand : IRequest<ExecuteAiActorThinkCycleResult>
{
    public required int ActorId { get; init; }
}

public record ExecuteAiActorThinkCycleResult
{
    public bool Success { get; init; }
    public int ExecutionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public string? DecisionSummary { get; init; }
    public List<string> Findings { get; init; } = new();
    public int QueriesAnalyzed { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int NotificationsTriggered { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public List<ExecutedActionInfo> Actions { get; init; } = new();
}

public record ExecutedActionInfo
{
    public AiActorActionType ActionType { get; init; }
    public string? Reasoning { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ResultEntityId { get; init; }
}
