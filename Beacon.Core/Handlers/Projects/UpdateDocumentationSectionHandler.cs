using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.Projects;

internal sealed class UpdateDocumentationSectionHandler(
    IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<UpdateDocumentationSectionCommand>
{
    public async Task Handle(
        UpdateDocumentationSectionCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var section = await context.ProjectDocumentationSections
            .Where(x => x.Id == request.SectionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (section == null)
        {
            throw new InvalidOperationException("Documentation section not found.");
        }

        section.Content = DocumentationContentParser.SanitizeMermaidDiagrams(request.Content);

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdateDocumentationSectionCommand(
    int SectionId,
    string Content) : IRequest;
