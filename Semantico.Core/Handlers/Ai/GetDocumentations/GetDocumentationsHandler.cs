using MediatR;
using Semantico.Core.Data.Entities;

namespace Semantico.Core.Handlers.Ai.GetDocumentations;

public record GetDocumentationsCommand : IRequest<List<DataSourceDocumentation>>
{
    public int DataSourceId { get; init; }
}
