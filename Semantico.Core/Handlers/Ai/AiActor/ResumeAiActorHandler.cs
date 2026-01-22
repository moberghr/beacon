using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record ResumeAiActorCommand : IRequest<ResumeAiActorResult>
{
    public required int ActorId { get; init; }
}

public record ResumeAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}
