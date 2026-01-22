using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.ArchiveDocumentation;

public record ArchiveDocumentationCommand : IRequest<Unit>
{
    public int DocumentationId { get; init; }
}
