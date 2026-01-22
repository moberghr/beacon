using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

internal sealed class ArchiveAiActorHandler : IRequestHandler<ArchiveAiActorCommand, ArchiveAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<ArchiveAiActorHandler> _logger;

    public ArchiveAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<ArchiveAiActorHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<ArchiveAiActorResult> Handle(
        ArchiveAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Archiving AI Actor {ActorId}", request.ActorId);

        await _aiActorService.ArchiveActorAsync(request.ActorId, cancellationToken);

        return new ArchiveAiActorResult
        {
            Success = true,
            ActorId = request.ActorId
        };
    }
}

