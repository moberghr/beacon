using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Authorization;
using Beacon.Core;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class CreateAiActorHandler : IRequestHandler<CreateAiActorCommand, CreateAiActorResult>
{
    private readonly AiActorService _aiActorService;
    private readonly ILogger<CreateAiActorHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;
    private readonly BeaconConfiguration _configuration;

    public CreateAiActorHandler(
        AiActorService aiActorService,
        ILogger<CreateAiActorHandler> logger,
        IBeaconActorAccessor actorAccessor,
        BeaconConfiguration configuration)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
        _configuration = configuration;
    }

    /// <summary>Why a caller who is not an Admin cannot create an actor whose changes do not wait for approval.</summary>
    public const string ApprovalOffRefusal = "Only an Admin can create an AI actor whose changes do not wait for approval.";

    public async Task<CreateAiActorResult> Handle(
        CreateAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating AI Actor '{Name}' for DataSource {DataSourceId}",
            request.Name, request.DataSourceId);

        // The creator is the signed-in caller, never a value from the request; an actor is never stored without one, and
        // with user management on its creator is a stored Beacon user (its think cycles act with the creator's access).
        var caller = await _actorAccessor.GetCurrentAsync(cancellationToken);
        var creatorUserId = ScheduledWorkOwner.Require(caller, _configuration);

        // An actor whose changes do not wait for approval runs what the model decides; no path changes that afterwards,
        // so only an Admin (live standing) creates one.
        if (request.RequiresApproval == false && !caller.IsAdmin)
        {
            throw AccessRefusal.Of(_logger, caller, "AI actor", null, ApprovalOffRefusal);
        }

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
            ActivateImmediately = request.ActivateImmediately ?? true,
            RequiresApproval = request.RequiresApproval ?? true
        };

        var actor = await _aiActorService.CreateActorAsync(options, cancellationToken);

        return new CreateAiActorResult
        {
            ActorId = actor.Id,
            Name = actor.Name,
            Status = actor.Status,
            DataSourceId = actor.DataSourceId,
            RequiresApproval = actor.RequiresApproval,
            CreatedTime = actor.CreatedTime
        };
    }
}

