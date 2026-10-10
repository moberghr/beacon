using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class CreateAiActorHandler : IRequestHandler<CreateAiActorCommand, CreateAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<CreateAiActorHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;

    public CreateAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<CreateAiActorHandler> logger,
        IBeaconActorAccessor actorAccessor)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
    }

    public async Task<CreateAiActorResult> Handle(
        CreateAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating AI Actor '{Name}' for DataSource {DataSourceId}",
            request.Name, request.DataSourceId);

        // The creator is the signed-in caller, never a value from the request; an actor is never stored without one.
        var creator = await _actorAccessor.GetCurrentAsync(cancellationToken);
        var creatorUserId = creator.UserId
            ?? throw new InvalidOperationException("The caller has no resolvable user to record as the AI actor's creator.");

        var options = new CreateAiActorOptions
        {
            Name = request.Name,
            Instructions = request.Instructions,
            DataSourceId = request.DataSourceId,
            AdditionalContext = request.AdditionalContext,
            MaxQueries = request.MaxQueries ?? 10,
            MaxSubscriptionsPerQuery = request.MaxSubscriptionsPerQuery ?? 3,
            CreatedByUserId = creatorUserId,
            DefaultRecipientIds = request.DefaultRecipientIds,
            ActivateImmediately = request.ActivateImmediately ?? true
        };

        var actor = await _aiActorService.CreateActorAsync(options, cancellationToken);

        return new CreateAiActorResult
        {
            ActorId = actor.Id,
            Name = actor.Name,
            Status = actor.Status,
            DataSourceId = actor.DataSourceId,
            CreatedTime = actor.CreatedTime
        };
    }
}

