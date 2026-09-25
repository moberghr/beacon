using Beacon.Core.Data;
using Beacon.Core.HostDocs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetImportedDocumentHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetImportedDocumentQuery, GetImportedDocumentResult>
{
    public async Task<GetImportedDocumentResult> Handle(GetImportedDocumentQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var document = await ImportedDocumentQueries.ById(context, request.ProjectId, request.DocumentId)
            .FirstOrDefaultAsync(cancellationToken);

        return new GetImportedDocumentResult(document);
    }
}

/// <summary>One host-imported document of a project, with its content. Null when it is not in that project.</summary>
public record GetImportedDocumentQuery(int ProjectId, int DocumentId) : IRequest<GetImportedDocumentResult>;

public record GetImportedDocumentResult(ImportedDocumentContent? Document);
