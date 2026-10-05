using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetProjectsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetProjectsQuery, PagedList<ProjectSummaryEntry>>
{
    public async Task<PagedList<ProjectSummaryEntry>> Handle(
        GetProjectsQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // ScanStatus renders as its name, so the page is mapped after it is read.
        var page = await context.Projects
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!))
            .Select(x =>
                new ProjectRow
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    DataSourceCount = x.DataSources.Count,
                    RepositoryCount = x.Repositories.Count,
                    LastScanAt = x.Repositories
                        .Where(y => y.LastScanAt != null)
                        .Max(y => (DateTime?)y.LastScanAt),
                    LastScanStatus = x.Repositories
                        .Where(y => y.LastScanAt != null)
                        .OrderByDescending(y => y.LastScanAt)
                        .Select(y => (ScanStatus?)y.ScanStatus)
                        .FirstOrDefault(),
                    CreatedAt = x.CreatedTime
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdAt");

        return page.Map(x => new ProjectSummaryEntry(
            x.Id,
            x.Name,
            x.Description,
            x.DataSourceCount,
            x.RepositoryCount,
            null, // QualityScore - computed from DataQualityScores if needed later
            x.LastScanStatus?.ToString(),
            x.LastScanAt,
            x.CreatedAt));
    }

    private sealed class ProjectRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        public int DataSourceCount { get; init; }

        public int RepositoryCount { get; init; }

        public DateTime? LastScanAt { get; init; }

        public ScanStatus? LastScanStatus { get; init; }

        public DateTime CreatedAt { get; init; }
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise; <c>search</c> matches the name — pickers send it as the user types.</summary>
public record GetProjectsQuery : ListRequest, IRequest<PagedList<ProjectSummaryEntry>>
{
    public string? Search { get; init; }
}

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
