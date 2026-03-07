using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.Projects;

internal sealed class GetProjectDetailHandler(SemanticoContext context)
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
                r.RepositoryUrl,
                r.ScanStatus,
                r.LastScanAt,
                r.TotalReferencesFound
            })
            .ToListAsync(cancellationToken);

        var repoEntries = repos.Select(r =>
        {
            var name = ExtractRepoName(r.RepositoryUrl);
            return new ProjectRepositoryEntry(
                name,
                r.RepositoryUrl,
                r.ScanStatus.ToString(),
                r.LastScanAt,
                r.TotalReferencesFound);
        }).ToList();

        var reports = await context.ProjectReports
            .Where(r => r.ProjectId == request.ProjectId)
            .OrderByDescending(r => r.GeneratedAt)
            .Select(r => new
            {
                r.GeneratedAt,
                r.ReportType
            })
            .ToListAsync(cancellationToken);

        var reportEntries = reports.Select(r => new ProjectReportEntry(
            r.GeneratedAt,
            0, // QualityScore - not stored on ProjectReport entity
            0, // TablesChecked - not stored
            0  // IssuesFound - not stored
        )).ToList();

        var schemaChanges = await context.SchemaChanges
            .Where(sc => context.ProjectDataSources
                .Where(pds => pds.ProjectId == request.ProjectId)
                .Select(pds => pds.DataSourceId)
                .Contains(sc.DataSourceId))
            .OrderByDescending(sc => sc.DetectedAt)
            .Select(sc => new
            {
                sc.ChangeType,
                sc.SchemaName,
                sc.TableName,
                sc.ColumnName,
                sc.OldValue,
                sc.NewValue,
                sc.DetectedAt
            })
            .ToListAsync(cancellationToken);

        var changeEntries = schemaChanges.Select(c => new ProjectSchemaChangeEntry(
            c.ChangeType,
            c.SchemaName,
            c.TableName,
            c.ColumnName,
            c.OldValue,
            c.NewValue,
            c.DetectedAt)).ToList();

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
            reportEntries,
            changeEntries);

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
    List<ProjectReportEntry> Reports,
    List<ProjectSchemaChangeEntry> SchemaChanges);

public record ProjectDataSourceEntry(string Name, string Type, int TableCount, double? QualityScore);

public record ProjectRepositoryEntry(string Name, string Url, string? ScanStatus, DateTime? LastScanAt, int ReferenceCount);

public record ProjectReportEntry(DateTime GeneratedAt, double QualityScore, int TablesChecked, int IssuesFound);

public record ProjectSchemaChangeEntry(
    SchemaChangeType ChangeType,
    string SchemaName,
    string TableName,
    string? ColumnName,
    string? OldValue,
    string? NewValue,
    DateTime DetectedAt);
