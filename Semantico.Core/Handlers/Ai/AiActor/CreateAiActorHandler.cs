using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class CreateAiActorHandler : IRequestHandler<CreateAiActorCommand, CreateAiActorResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<CreateAiActorHandler> _logger;

    public CreateAiActorHandler(
        IAiActorService aiActorService,
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

public record CreateAiActorCommand : IRequest<CreateAiActorResult>
{
    public required string Name { get; init; }
    public required string Instructions { get; init; }
    public required int DataSourceId { get; init; }
    public string? AdditionalContext { get; init; }
    public int? MaxQueries { get; init; }
    public int? MaxSubscriptionsPerQuery { get; init; }
    public string? CreatedByUserId { get; init; }
    public List<int>? DefaultRecipientIds { get; init; }
    public bool? ActivateImmediately { get; init; }
}

public record CreateAiActorResult
{
    public int ActorId { get; init; }
    public string Name { get; init; } = null!;
    public AiActorStatus Status { get; init; }
    public int DataSourceId { get; init; }
    public DateTime CreatedTime { get; init; }
}
