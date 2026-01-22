using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record ArchiveAiActorCommand : IRequest<ArchiveAiActorResult>
{
    public required int ActorId { get; init; }
}

public record ArchiveAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}
