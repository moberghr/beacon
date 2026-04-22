using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Ai.AiActor;

public record PauseAiActorCommand : IRequest<PauseAiActorResult>
{
    public required int ActorId { get; init; }
}

public record PauseAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}
