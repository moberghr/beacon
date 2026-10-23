using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetProjectDetailHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IMcpSettingsProvider settingsProvider,
    IOptions<ProjectAuthorizationOptions> options)
    : IRequestHandler<GetProjectDetailQuery, GetProjectDetailResult>
{
    public async Task<GetProjectDetailResult> Handle(
        GetProjectDetailQuery request,
        CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var project = await context.Projects
            .Where(p => p.Id == request.ProjectId && p.ArchivedTime == null)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.CreatedTime,
                p.RequiresMembership
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
            return new GetProjectDetailResult(null);

        var dataSources = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == request.ProjectId)
            .Select(pds => new
            {
                pds.DataSourceId,
                pds.DataSource.Name,
                pds.DataSource.DataSourceType,
                TableCount = context.DatabaseMetadata
                    .Count(dm => dm.DataSourceId == pds.DataSourceId && dm.ArchivedTime == null)
            })
            .ToListAsync(cancellationToken);

        var dsEntries = dataSources.Select(d => new ProjectDataSourceEntry(
            d.DataSourceId,
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
                r.LastScanError,
                HasAccessToken = r.EncryptedAccessToken != null
            })
            .ToListAsync(cancellationToken);

        var repoEntries = repos.Select(r =>
        {
            var name = ExtractRepoName(r.RepositoryUrl);
            var partialReasons = RepositoryScanNotes.PartialReasons(r.ScanStatus, r.LastScanError);
            return new ProjectRepositoryEntry(
                r.Id,
                name,
                r.RepositoryUrl,
                r.ScanStatus.ToString(),
                r.LastScanAt,
                r.TotalReferencesFound,
                r.HasAccessToken)
            {
                PartialScan = partialReasons.Count > 0,
                PartialScanReasons = partialReasons
            };
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

        var mayManageMembers = scope.Allows(project.Id, ProjectAction.ManageMembers);
        var openlyReachable = mayManageMembers
            ? await OpenlyReachableDataSourceIdsAsync(context, project.Id, cancellationToken)
            : [];

        // Who else reaches this project's data sources names other (possibly restricted) projects: an Admin's view only.
        var sharedSourceProjects = scope.IsUnrestricted
            ? await SharedSourceProjects.ReadAsync(context, project.Id, options.Value.RequireProjectMembership, cancellationToken)
            : [];
        var sharedSourceGuardrails = scope.IsUnrestricted && sharedSourceProjects.Count > 0
            ? await SharedSourceGuardrails.ReadAsync(context, settingsProvider, project.Id, cancellationToken)
            : [];

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
            hasDocumentation,
            project.RequiresMembership || options.Value.RequireProjectMembership,
            scope.RoleIn(project.Id),
            mayManageMembers && ProjectMembershipRules.IsManagedByBeacon(membershipSource),
            openlyReachable,
            sharedSourceProjects,
            sharedSourceGuardrails,
            ProjectMembershipRules.ManagementOf(membershipSource));

        return new GetProjectDetailResult(detail);
    }

    // The project's data sources that another live open project also links: restricting this project leaves them
    // reachable through that one. None while every project is restricted.
    private async Task<List<int>> OpenlyReachableDataSourceIdsAsync(BeaconContext context, int projectId, CancellationToken cancellationToken)
    {
        if (options.Value.RequireProjectMembership)
        {
            return [];
        }

        return await context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .Where(x => context.ProjectDataSources
                .Where(y => y.DataSourceId == x.DataSourceId)
                .Where(y => y.ProjectId != projectId)
                .Where(y => !y.Project.RequiresMembership)
                .Any(y => y.Project.ArchivedTime == null))
            .Select(x => x.DataSourceId)
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    private static string ExtractRepoName(string url)
    {
        var uri = url.TrimEnd('/');
        var lastSlash = uri.LastIndexOf('/');
        return lastSlash >= 0 ? uri[(lastSlash + 1)..] : uri;
    }
}

public record GetProjectDetailQuery(int ProjectId) : IRequest<GetProjectDetailResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Read)
    ];
}

public record GetProjectDetailResult(ProjectDetailEntry? Project);

/// <summary>
/// A project's detail. <c>RequiresMembership</c>: restricted; <c>MyRole</c>: the caller's effective role in it;
/// <c>CanManageMembers</c>: the caller may manage its members and Beacon (not the host) manages membership;
/// <c>OpenlyReachableDataSourceIds</c>: for a caller who may manage its members, its data sources another live open
/// project also links (they stay reachable when this one is restricted); <c>SharedSourceProjects</c>: for an Admin, every
/// other live project (open or restricted) linking any of its data sources, with their ProjectAdmins (empty for anyone
/// else); <c>SharedSourceGuardrails</c>: for an Admin, per data source another live project links too, the guardrails
/// that apply to it — the strictest of the linking projects' settings — and the project that sets each (empty for anyone
/// else); <c>MembershipManagement</c>: who manages project memberships in this deployment (Beacon, the host, or nobody
/// because membership is unavailable).
/// </summary>
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
    bool HasDocumentation,
    bool RequiresMembership,
    ProjectRole? MyRole,
    bool CanManageMembers,
    List<int> OpenlyReachableDataSourceIds,
    List<SharedSourceProjectEntry> SharedSourceProjects,
    List<SharedSourceGuardrailsEntry> SharedSourceGuardrails,
    ProjectMembershipManagement MembershipManagement);

public record ProjectDataSourceEntry(int Id, string Name, string Type, int TableCount, double? QualityScore);

public record ProjectRepositoryEntry(int Id, string Name, string Url, string? ScanStatus, DateTime? LastScanAt, int ReferenceCount, bool HasAccessToken)
{
    /// <summary>The last scan completed but hit a limit (files, file size or time): the references are incomplete.</summary>
    public bool PartialScan { get; init; }

    /// <summary>Why the last scan was partial; empty for a full scan.</summary>
    public List<string> PartialScanReasons { get; init; } = [];
}
