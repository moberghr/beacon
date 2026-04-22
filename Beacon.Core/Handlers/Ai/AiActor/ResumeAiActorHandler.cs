using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Ai.AiActor;

public record ResumeAiActorCommand : IRequest<ResumeAiActorResult>
{
    public required int ActorId { get; init; }
}

public record ResumeAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}
