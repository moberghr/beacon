using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.Projects;

internal sealed class GetProjectsHandler(SemanticoContext context)
    : IRequestHandler<GetProjectsQuery, GetProjectsResult>
{
    public async Task<GetProjectsResult> Handle(
        GetProjectsQuery request,
        CancellationToken cancellationToken)
    {
        var rows = await context.Projects
            .Where(p => p.ArchivedTime == null)
            .OrderByDescending(p => p.CreatedTime)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                DataSourceCount = p.DataSources.Count,
                RepositoryCount = p.Repositories.Count,
                LastScannedRepo = p.Repositories
                    .Where(r => r.LastScanAt != null)
                    .OrderByDescending(r => r.LastScanAt)
                    .Select(r => new { r.LastScanAt, r.ScanStatus })
                    .FirstOrDefault(),
                p.CreatedTime
            })
            .ToListAsync(cancellationToken);

        var entries = rows.Select(r => new ProjectSummaryEntry(
            r.Id,
            r.Name,
            r.Description,
            r.DataSourceCount,
            r.RepositoryCount,
            null, // QualityScore - computed from DataQualityScores if needed later
            r.LastScannedRepo?.ScanStatus.ToString(),
            r.LastScannedRepo?.LastScanAt,
            r.CreatedTime)).ToList();

        return new GetProjectsResult(entries);
    }
}

public record GetProjectsQuery : IRequest<GetProjectsResult>;

public record GetProjectsResult(List<ProjectSummaryEntry> Entries);

public record ProjectSummaryEntry(
    int Id,
    string Name,
    string? Description,
    int DataSourceCount,
    int RepositoryCount,
    double? QualityScore,
    string? LastScanStatus,
    DateTime? LastScanAt,
    DateTime CreatedAt);
