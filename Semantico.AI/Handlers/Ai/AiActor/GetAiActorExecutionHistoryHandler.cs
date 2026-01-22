using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

internal sealed class GetAiActorExecutionHistoryHandler : IRequestHandler<GetAiActorExecutionHistoryQuery, GetAiActorExecutionHistoryResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<GetAiActorExecutionHistoryHandler> _logger;

    public GetAiActorExecutionHistoryHandler(
        IAiActorServiceExtended aiActorService,
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

