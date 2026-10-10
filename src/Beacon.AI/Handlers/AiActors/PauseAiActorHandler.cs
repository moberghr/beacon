using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class PauseAiActorHandler : IRequestHandler<PauseAiActorCommand, PauseAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<PauseAiActorHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public PauseAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<PauseAiActorHandler> logger,
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
        _contextFactory = contextFactory;
    }

    public async Task<PauseAiActorResult> Handle(
        PauseAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pausing AI Actor {ActorId}", request.ActorId);

        await AiActorOwnership.EnsureCreatorOrAdminAsync(_actorAccessor, _contextFactory, request.ActorId, _logger, cancellationToken);

        await _aiActorService.PauseActorAsync(request.ActorId, cancellationToken);

        return new PauseAiActorResult
        {
            Success = true,
            ActorId = request.ActorId
        };
    }
}

