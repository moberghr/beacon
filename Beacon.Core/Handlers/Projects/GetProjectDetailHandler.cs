using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetProjectDetailHandler(BeaconContext context)
    : IRequestHandler<GetProjectDetailQuery, GetProjectDetailResult>
{
    public async Task<GetProjectDetailResult> Handle(
        GetProjectDetailQuery request,
        CancellationToken cancellationToken)
    {
        var project = await context.Projects
            .Where(p => p.Id == request.ProjectId && p.ArchivedTime == null)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.CreatedTime
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
            return new GetProjectDetailResult(null);

        var dataSources = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == request.ProjectId)
            .Select(pds => new
            {
                pds.DataSource.Name,
                pds.DataSource.DataSourceType,
                TableCount = context.DatabaseMetadata
                    .Count(dm => dm.DataSourceId == pds.DataSourceId && dm.ArchivedTime == null)
            })
            .ToListAsync(cancellationToken);

        var dsEntries = dataSources.Select(d => new ProjectDataSourceEntry(
            d.Name,
            d.DataSourceType.ToString(),
            d.TableCount,
            null // QualityScore - can be aggregated later
        )).ToList();

        var repos = await context.GitHubRepositories
            .Where(r => r.ProjectId == request.ProjectId)
            .Select(r => new
            {
                r.Id,
                r.RepositoryUrl,
                r.ScanStatus,
                r.LastScanAt,
                r.TotalReferencesFound,
                HasAccessToken = r.EncryptedAccessToken != null
            })
            .ToListAsync(cancellationToken);

        var repoEntries = repos.Select(r =>
        {
            var name = ExtractRepoName(r.RepositoryUrl);
            return new ProjectRepositoryEntry(
                r.Id,
                name,
                r.RepositoryUrl,
                r.ScanStatus.ToString(),
                r.LastScanAt,
                r.TotalReferencesFound,
                r.HasAccessToken);
        }).ToList();

        var hasDocumentation = await context.ProjectDocumentations
            .AnyAsync(d => d.ProjectId == request.ProjectId, cancellationToken);

        var totalTables = dsEntries.Sum(d => d.TableCount);
        var codeRefCount = repos.Sum(r => r.TotalReferencesFound);
        var lastScan = repos
            .Where(r => r.LastScanAt.HasValue)
            .Select(r => r.LastScanAt)
            .OrderByDescending(d => d)
            .FirstOrDefault();

        var detail = new ProjectDetailEntry(
            project.Id,
            project.Name,
            project.Description,
            totalTables,
            null, // QualityScore
            codeRefCount,
            lastScan,
            dsEntries,
            repoEntries,
            hasDocumentation);

        return new GetProjectDetailResult(detail);
    }

    private static string ExtractRepoName(string url)
    {
        var uri = url.TrimEnd('/');
        var lastSlash = uri.LastIndexOf('/');
        return lastSlash >= 0 ? uri[(lastSlash + 1)..] : uri;
    }
}

public record GetProjectDetailQuery(int ProjectId) : IRequest<GetProjectDetailResult>;

public record GetProjectDetailResult(ProjectDetailEntry? Project);

public record ProjectDetailEntry(
    int Id,
    string Name,
    string? Description,
    int TotalTables,
    double? QualityScore,
    int CodeReferenceCount,
    DateTime? LastScanAt,
    List<ProjectDataSourceEntry> DataSources,
    List<ProjectRepositoryEntry> Repositories,
    bool HasDocumentation);

public record ProjectDataSourceEntry(string Name, string Type, int TableCount, double? QualityScore);

public record ProjectRepositoryEntry(int Id, string Name, string Url, string? ScanStatus, DateTime? LastScanAt, int ReferenceCount, bool HasAccessToken);
