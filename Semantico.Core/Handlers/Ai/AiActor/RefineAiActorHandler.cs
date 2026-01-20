using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Ai.AiActor;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class RefineAiActorHandler : IRequestHandler<RefineAiActorCommand, RefineAiActorResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<RefineAiActorHandler> _logger;

    public RefineAiActorHandler(
        IAiActorService aiActorService,
        ILogger<RefineAiActorHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<RefineAiActorResult> Handle(
        RefineAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Refining AI Actor {ActorId} with user feedback", request.ActorId);

        var result = await _aiActorService.RefineActorAsync(
            request.ActorId,
            request.Feedback,
            cancellationToken);

        return new RefineAiActorResult
        {
            Success = result.Success,
            ExecutionId = result.ExecutionId,
            Phase = result.Phase,
            DecisionSummary = result.DecisionSummary,
            QueriesCreated = result.QueriesCreated,
            QueriesRefined = result.QueriesRefined,
            SubscriptionsCreated = result.SubscriptionsCreated,
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            ErrorMessage = result.ErrorMessage
        };
    }
}

public record RefineAiActorCommand : IRequest<RefineAiActorResult>
{
    public required int ActorId { get; init; }
    public required string Feedback { get; init; }
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
