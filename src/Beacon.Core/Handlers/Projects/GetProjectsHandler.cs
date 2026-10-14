using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ApiKeys;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetProjectsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    IOptions<ProjectAuthorizationOptions> options)
    : IRequestHandler<GetProjectsQuery, PagedList<ProjectSummaryEntry>>
{
    public async Task<PagedList<ProjectSummaryEntry>> Handle(
        GetProjectsQuery request,
        CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var listed = request.ForApiKey == true
            ? await ApiKeyProjects.ScopeOfOwnKeyAsync(projectAccess, await actorAccessor.GetCurrentAsync(cancellationToken), cancellationToken)
            : scope;
        var requiresMembershipEverywhere = options.Value.RequireProjectMembership;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // ScanStatus renders as its name, so the page is mapped after it is read; the caller's role comes from its scope.
        var page = await context.Projects
            .WhereProjectIn(listed.For(ProjectAction.Read), x => x.Id)
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
                    CreatedAt = x.CreatedTime,
                    RequiresMembership = x.RequiresMembership,
                    MemberCount = x.Members.Count
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
            x.CreatedAt,
            x.RequiresMembership || requiresMembershipEverywhere,
            scope.RoleIn(x.Id),
            x.MemberCount));
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

        public bool RequiresMembership { get; init; }

        public int MemberCount { get; init; }
    }
}

/// <summary>
/// Newest first unless <c>sort</c> says otherwise; <c>search</c> matches the name — pickers send it as the user types.
/// <c>forApiKey</c> lists only the projects a new API key of the caller's may name: the ones it reads without the Admin
/// bypass, which keys never carry.
/// </summary>
public record GetProjectsQuery : ListRequest, IRequest<PagedList<ProjectSummaryEntry>>, IProjectFilteredRequest
{
    public string? Search { get; init; }

    public bool? ForApiKey { get; init; }
}

/// <summary>
/// A project in the list. <c>RequiresMembership</c>: restricted (members and Admins only); <c>MyRole</c>: the caller's
/// effective role in it; <c>MemberCount</c>: its membership rows.
/// </summary>
public record ProjectSummaryEntry(
    int Id,
    string Name,
    string? Description,
    int DataSourceCount,
    int RepositoryCount,
    double? QualityScore,
    string? LastScanStatus,
    DateTime? LastScanAt,
    DateTime CreatedAt,
    bool RequiresMembership,
    ProjectRole? MyRole,
    int MemberCount);
