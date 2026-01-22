using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.UnarchiveDocumentation;

public record UnarchiveDocumentationCommand : IRequest<Unit>
{
    public int DocumentationId { get; init; }
}
