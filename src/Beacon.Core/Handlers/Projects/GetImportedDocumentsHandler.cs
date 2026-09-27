using Beacon.Core.Data;
using Beacon.Core.HostDocs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetImportedDocumentsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetImportedDocumentsQuery, GetImportedDocumentsResult>
{
    public async Task<GetImportedDocumentsResult> Handle(GetImportedDocumentsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var documents = await ImportedDocumentQueries.ListForProject(context, request.ProjectId)
            .ToListAsync(cancellationToken);

        return new GetImportedDocumentsResult(documents);
    }
}

/// <summary>The host-imported documents (ExposeDocs) of a project, without their content.</summary>
public record GetImportedDocumentsQuery(int ProjectId) : IRequest<GetImportedDocumentsResult>;

public record GetImportedDocumentsResult(List<ImportedDocumentSummary> Documents);
