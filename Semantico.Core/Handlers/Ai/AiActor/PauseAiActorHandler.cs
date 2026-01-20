using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services.Ai.AiActor;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class PauseAiActorHandler : IRequestHandler<PauseAiActorCommand, PauseAiActorResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<PauseAiActorHandler> _logger;

    public PauseAiActorHandler(
        IAiActorService aiActorService,
        ILogger<PauseAiActorHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<PauseAiActorResult> Handle(
        PauseAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pausing AI Actor {ActorId}", request.ActorId);

        await _aiActorService.PauseActorAsync(request.ActorId, cancellationToken);

        return new PauseAiActorResult
        {
            Success = true,
            ActorId = request.ActorId
        };
    }
}

public record PauseAiActorCommand : IRequest<PauseAiActorResult>
{
    public required int ActorId { get; init; }
}

public record PauseAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}
