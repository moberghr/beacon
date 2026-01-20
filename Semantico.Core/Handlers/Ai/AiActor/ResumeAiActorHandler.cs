using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services.Ai.AiActor;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class ResumeAiActorHandler : IRequestHandler<ResumeAiActorCommand, ResumeAiActorResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<ResumeAiActorHandler> _logger;

    public ResumeAiActorHandler(
        IAiActorService aiActorService,
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

public record ResumeAiActorCommand : IRequest<ResumeAiActorResult>
{
    public required int ActorId { get; init; }
}

public record ResumeAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}
