using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Ai.AiActor;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class GetAiActorExecutionHistoryHandler : IRequestHandler<GetAiActorExecutionHistoryQuery, GetAiActorExecutionHistoryResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<GetAiActorExecutionHistoryHandler> _logger;

    public GetAiActorExecutionHistoryHandler(
        IAiActorService aiActorService,
        ILogger<GetAiActorExecutionHistoryHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<GetAiActorExecutionHistoryResult> Handle(
        GetAiActorExecutionHistoryQuery request,
        CancellationToken cancellationToken)
    {
        var executions = await _aiActorService.GetExecutionHistoryAsync(
            request.ActorId,
            request.Limit,
            cancellationToken);

        return new GetAiActorExecutionHistoryResult
        {
            ActorId = request.ActorId,
            Executions = executions.Select(e => new ExecutionHistoryItem
            {
                ExecutionId = e.Id,
                TriggeringSubscriptionId = e.TriggeringSubscriptionId,
                Phase = e.Phase,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                QueriesAnalyzed = e.QueriesAnalyzed,
                QueriesCreated = e.QueriesCreated,
                QueriesRefined = e.QueriesRefined,
                SubscriptionsCreated = e.SubscriptionsCreated,
                NotificationsTriggered = e.NotificationsTriggered,
                TokensUsed = e.TokensUsed,
                EstimatedCost = e.EstimatedCost,
                Model = e.Model,
                DecisionSummary = e.DecisionSummary,
                ErrorMessage = e.ErrorMessage,
                ActionsJson = e.ActionsJson
            }).ToList()
        };
    }
}

public record GetAiActorExecutionHistoryQuery : IRequest<GetAiActorExecutionHistoryResult>
{
    public required int ActorId { get; init; }
    public int? Limit { get; init; }
}

public record GetAiActorExecutionHistoryResult
{
    public int ActorId { get; init; }
    public List<ExecutionHistoryItem> Executions { get; init; } = new();
}

public record ExecutionHistoryItem
{
    public int ExecutionId { get; init; }
    public int? TriggeringSubscriptionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int QueriesAnalyzed { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int NotificationsTriggered { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? Model { get; init; }
    public string? DecisionSummary { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ActionsJson { get; init; }
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
}
