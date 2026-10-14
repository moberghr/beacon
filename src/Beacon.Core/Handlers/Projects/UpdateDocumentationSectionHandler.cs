using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// Replaces a documentation section's content (grounding for the project's prompts). The edit is recorded in the security
/// audit, by section and project and with the content's length (never the content), in the same save.
/// </summary>
internal sealed class UpdateDocumentationSectionHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISecurityAuditRecorder securityAudit)
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

        var projectId = await context.ProjectDocumentations
            .Where(x => x.Id == section.ProjectDocumentationId)
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.DocumentationSectionEdited)
                .On(SecurityAuditResources.DocumentationSection, section.Id)
                .InProject(projectId)
                .With(new SecurityAuditDetails()
                    .Add("documentationId", section.ProjectDocumentationId)
                    .Add("contentLength", section.Content.Length)),
            cancellationToken);
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
