using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Authorization;

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

/// <summary>Replaces a documentation section's content: grounding for the project's prompts, so it needs Curate there.</summary>
public record UpdateDocumentationSectionCommand(
    int SectionId,
    string Content) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DocumentationSection(SectionId), ProjectAction.Curate)
    ];
}
