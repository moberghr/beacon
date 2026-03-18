using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.Projects;

public record GetProjectDocumentationQuery(int ProjectId) : IRequest<GetProjectDocumentationResult>;

public record GetProjectDocumentationResult(ProjectDocumentationDetailEntry? Latest, List<ProjectDocumentationHistoryEntry> History);

public record ProjectDocumentationDetailEntry(
    int Id,
    DateTime GeneratedAt,
    string GeneratedByModel,
    int DataSourcesAnalyzed,
    int TablesAnalyzed,
    int CodeReferencesAnalyzed,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    TimeSpan GenerationDuration,
    List<ProjectDocSectionEntry> Sections);

public record ProjectDocSectionEntry(
    int Id,
    ProjectDocSectionType SectionType,
    string Title,
    string Content,
    int SortOrder);

public record ProjectDocumentationHistoryEntry(
    int Id,
    DateTime GeneratedAt,
    string GeneratedByModel,
    int TablesAnalyzed,
    int SectionsCount,
    int TotalTokens,
    decimal EstimatedCost);

internal sealed class GetProjectDocumentationHandler(
    IDbContextFactory<SemanticoContext> contextFactory) : IRequestHandler<GetProjectDocumentationQuery, GetProjectDocumentationResult>
{
    public async Task<GetProjectDocumentationResult> Handle(
        GetProjectDocumentationQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get all documentations for this project, ordered by newest first
        var documentations = await context.ProjectDocumentations
            .Where(d => d.ProjectId == request.ProjectId)
            .OrderByDescending(d => d.GeneratedAt)
            .Select(d => new
            {
                d.Id,
                d.GeneratedAt,
                d.GeneratedByModel,
                d.DataSourcesAnalyzed,
                d.TablesAnalyzed,
                d.CodeReferencesAnalyzed,
                d.InputTokens,
                d.OutputTokens,
                d.EstimatedCost,
                d.GenerationDuration,
                Sections = d.Sections
                    .OrderBy(s => s.SortOrder)
                    .Select(s => new ProjectDocSectionEntry(
                        s.Id, s.SectionType, s.Title, s.Content, s.SortOrder))
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        if (documentations.Count == 0)
            return new GetProjectDocumentationResult(null, new List<ProjectDocumentationHistoryEntry>());

        var latest = documentations[0];
        var latestDetail = new ProjectDocumentationDetailEntry(
            latest.Id, latest.GeneratedAt, latest.GeneratedByModel,
            latest.DataSourcesAnalyzed, latest.TablesAnalyzed, latest.CodeReferencesAnalyzed,
            latest.InputTokens, latest.OutputTokens, latest.EstimatedCost,
            latest.GenerationDuration, latest.Sections);

        var history = documentations.Select(d => new ProjectDocumentationHistoryEntry(
            d.Id, d.GeneratedAt, d.GeneratedByModel, d.TablesAnalyzed,
            d.Sections.Count, d.InputTokens + d.OutputTokens, d.EstimatedCost))
            .ToList();

        return new GetProjectDocumentationResult(latestDetail, history);
    }
}
