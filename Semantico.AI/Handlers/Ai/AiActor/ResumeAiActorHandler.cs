using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

internal sealed class ResumeAiActorHandler : IRequestHandler<ResumeAiActorCommand, ResumeAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<ResumeAiActorHandler> _logger;

    public ResumeAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<ResumeAiActorHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<ResumeAiActorResult> Handle(
        ResumeAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Resuming AI Actor {ActorId}", request.ActorId);

        await _aiActorService.ResumeActorAsync(request.ActorId, cancellationToken);

        return new ResumeAiActorResult
        {
            Success = true,
            ActorId = request.ActorId
        };
    }
}

