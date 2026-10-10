using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class ResumeAiActorHandler : IRequestHandler<ResumeAiActorCommand, ResumeAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<ResumeAiActorHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public ResumeAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<ResumeAiActorHandler> logger,
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
        _contextFactory = contextFactory;
    }

    public async Task<ResumeAiActorResult> Handle(
        ResumeAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Resuming AI Actor {ActorId}", request.ActorId);

        await AiActorOwnership.EnsureCreatorOrAdminAsync(_actorAccessor, _contextFactory, request.ActorId, _logger, cancellationToken);

        await _aiActorService.ResumeActorAsync(request.ActorId, cancellationToken);

        return new ResumeAiActorResult
        {
            Success = true,
            ActorId = request.ActorId
        };
    }
}

