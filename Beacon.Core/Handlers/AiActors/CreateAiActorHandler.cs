using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.AiActors;

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
