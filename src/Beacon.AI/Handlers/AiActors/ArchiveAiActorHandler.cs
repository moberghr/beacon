using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class ArchiveAiActorHandler : IRequestHandler<ArchiveAiActorCommand, ArchiveAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<ArchiveAiActorHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public ArchiveAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<ArchiveAiActorHandler> logger,
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
        _contextFactory = contextFactory;
    }

    public async Task<ArchiveAiActorResult> Handle(
        ArchiveAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Archiving AI Actor {ActorId}", request.ActorId);

        await AiActorOwnership.EnsureCreatorOrAdminAsync(_actorAccessor, _contextFactory, request.ActorId, _logger, cancellationToken);

        await _aiActorService.ArchiveActorAsync(request.ActorId, cancellationToken);

        return new ArchiveAiActorResult
        {
            Success = true,
            ActorId = request.ActorId
        };
    }
}

