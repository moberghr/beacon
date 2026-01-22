using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.AI.Services.Ai.AiActor.Models;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

internal sealed class CreateAiActorHandler : IRequestHandler<CreateAiActorCommand, CreateAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<CreateAiActorHandler> _logger;

    public CreateAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<CreateAiActorHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<CreateAiActorResult> Handle(
        CreateAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating AI Actor '{Name}' for DataSource {DataSourceId}",
            request.Name, request.DataSourceId);

        var options = new CreateAiActorOptions
        {
            Name = request.Name,
            Instructions = request.Instructions,
            DataSourceId = request.DataSourceId,
            AdditionalContext = request.AdditionalContext,
            MaxQueries = request.MaxQueries ?? 10,
            MaxSubscriptionsPerQuery = request.MaxSubscriptionsPerQuery ?? 3,
            CreatedByUserId = request.CreatedByUserId,
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

