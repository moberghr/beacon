using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Authorization;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// Replaces a documentation section's content (grounding for the project's prompts) and marks the section curated: who
/// edited it (<see cref="Data.Entities.Projects.ProjectDocumentationSection.EditedByUserId"/>, null for a caller without a
/// stored Beacon user) and when, so regenerating the documentation carries it forward instead of replacing it. The edit is
/// recorded in the security audit, by section and project and with the content's length (never the content), in the same
/// save.
/// </summary>
internal sealed class UpdateDocumentationSectionHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISecurityAuditRecorder securityAudit,
    IBeaconActorAccessor actorAccessor,
    TimeProvider timeProvider)
    : IRequestHandler<UpdateDocumentationSectionCommand>
{
    internal const string SupersededMessage =
        "The documentation was regenerated since this section was loaded: reload the documentation and edit the section again.";

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

        var projectId = await context.ProjectDocumentations
            .Where(x => x.Id == section.ProjectDocumentationId)
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);
        var latestId = await context.ProjectDocumentations
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.GeneratedAt)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestId != section.ProjectDocumentationId)
        {
            throw new BeaconConflictException(SupersededMessage);
        }

        if (request.ReleaseToGenerated)
        {
            section.EditedByUserId = null;
            section.EditedTime = null;
        }
        else
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
            section.Content = DocumentationContentParser.SanitizeMermaidDiagrams(request.Content);
            section.EditedByUserId = actor.BeaconUserId;
            section.EditedTime = timeProvider.GetUtcNow().UtcDateTime;
        }

        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.DocumentationSectionEdited)
                .On(SecurityAuditResources.DocumentationSection, section.Id)
                .InProject(projectId)
                .With(new SecurityAuditDetails()
                    .Add("documentationId", section.ProjectDocumentationId)
                    .Add("contentLength", section.Content.Length)
                    .Add("released", request.ReleaseToGenerated)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Replaces a documentation section's content, or with <paramref name="ReleaseToGenerated"/> releases it: the content stays,
/// the section is no longer curated, so the next regeneration replaces it. Only a section of the project's latest
/// documentation is edited (409 otherwise). Grounding for the project's prompts, so it needs Curate there.
/// </summary>
public record UpdateDocumentationSectionCommand(
    int SectionId,
    string Content,
    bool ReleaseToGenerated = false) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DocumentationSection(SectionId), ProjectAction.Curate)
    ];
}
