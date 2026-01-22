using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

internal sealed class PauseAiActorHandler : IRequestHandler<PauseAiActorCommand, PauseAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<PauseAiActorHandler> _logger;

    public PauseAiActorHandler(
        IAiActorServiceExtended aiActorService,
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

