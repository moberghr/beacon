using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Embed;

/// <summary>
/// The embed grants in the projects the caller reads, filtered in the SQL (newest first unless <c>sort</c> says otherwise).
/// A grant's status is <see cref="EmbedGrantStatuses.Revoked"/>, <see cref="EmbedGrantStatuses.Expired"/>,
/// <see cref="EmbedGrantStatuses.Superseded"/> (its query has another active version, or was archived) or
/// <see cref="EmbedGrantStatuses.Active"/>, in that order. Creators are named by <see cref="UserLabel"/>; no secret or
/// secret hash is ever returned.
/// </summary>
internal sealed class ListEmbedGrantsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    TimeProvider timeProvider)
    : IRequestHandler<ListEmbedGrantsQuery, PagedList<EmbedGrantListItem>>
{
    public async Task<PagedList<EmbedGrantListItem>> Handle(ListEmbedGrantsQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await BuildQuery(context, readable, now, request.ProjectId)
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");

        return page.Map(x =>
            new EmbedGrantListItem
            {
                Id = x.Id,
                PublicId = x.PublicId,
                Name = x.Name,
                ProjectId = x.ProjectId,
                ProjectName = x.ProjectName,
                QueryId = x.QueryId,
                QueryName = x.QueryName,
                QueryVersionId = x.QueryVersionId,
                VersionNumber = x.VersionNumber,
                AllowedOrigins = JsonSerializer.Deserialize<List<string>>(x.AllowedOriginsJson) ?? [],
                LockedParameters = JsonSerializer.Deserialize<List<string>>(x.LockedParameterNamesJson) ?? [],
                CreatedByLabel = x.CreatedByLabel,
                CreatedTime = x.CreatedTime,
                ExpiresTime = x.ExpiresTime,
                RevokedTime = x.RevokedTime,
                LastTokenIssuedTime = x.LastTokenIssuedTime,
                Status = x.Status
            });
    }

    internal static IQueryable<EmbedGrantRow> BuildQuery(BeaconContext context, ProjectSet readable, DateTime now, int? projectId)
    {
        var grants = context.EmbedGrants
            .WhereProjectIn(readable, x => x.ProjectId);

        if (projectId.HasValue)
        {
            grants = grants.Where(x => x.ProjectId == projectId.Value);
        }

        return grants
            .Select(x =>
                new EmbedGrantRow
                {
                    Id = x.Id,
                    PublicId = x.PublicId,
                    Name = x.Name,
                    ProjectId = x.ProjectId,
                    ProjectName = context.Projects
                        .Where(y => y.Id == x.ProjectId)
                        .Select(y => y.Name)
                        .FirstOrDefault(),
                    QueryId = x.QueryId,
                    QueryName = context.Queries
                        .Where(y => y.Id == x.QueryId)
                        .Select(y => y.Name)
                        .FirstOrDefault(),
                    QueryVersionId = x.QueryVersionId,
                    VersionNumber = x.QueryVersion.VersionNumber,
                    AllowedOriginsJson = x.AllowedOriginsJson,
                    LockedParameterNamesJson = x.LockedParameterNamesJson,
                    CreatedByLabel = context.Users
                        .Where(y => y.ExternalId == x.CreatedByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault(),
                    CreatedTime = x.CreatedTime,
                    ExpiresTime = x.ExpiresTime,
                    RevokedTime = x.RevokedTime,
                    LastTokenIssuedTime = x.LastTokenIssuedTime,
                    Status = x.RevokedTime != null
                        ? EmbedGrantStatuses.Revoked
                        : x.ExpiresTime <= now
                            ? EmbedGrantStatuses.Expired
                            : context.Queries
                                .Where(y => y.Id == x.QueryId)
                                .Any(y => y.ActiveVersionId == x.QueryVersionId)
                                ? EmbedGrantStatuses.Active
                                : EmbedGrantStatuses.Superseded
                });
    }
}

/// <summary>The status names a listed grant carries.</summary>
public static class EmbedGrantStatuses
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string Superseded = "superseded";
}

internal sealed class EmbedGrantRow
{
    public int Id { get; init; }

    public string PublicId { get; init; } = null!;

    public string Name { get; init; } = null!;

    public int ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public int QueryId { get; init; }

    public string? QueryName { get; init; }

    public int QueryVersionId { get; init; }

    public int VersionNumber { get; init; }

    public string AllowedOriginsJson { get; init; } = "[]";

    public string LockedParameterNamesJson { get; init; } = "[]";

    public string? CreatedByLabel { get; init; }

    public DateTime CreatedTime { get; init; }

    public DateTime ExpiresTime { get; init; }

    public DateTime? RevokedTime { get; init; }

    public DateTime? LastTokenIssuedTime { get; init; }

    public string Status { get; init; } = null!;
}

/// <summary>One embed grant as the management list shows it.</summary>
public sealed class EmbedGrantListItem
{
    public int Id { get; init; }

    public string PublicId { get; init; } = null!;

    public string Name { get; init; } = null!;

    public int ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public int QueryId { get; init; }

    public string? QueryName { get; init; }

    public int QueryVersionId { get; init; }

    public int VersionNumber { get; init; }

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> LockedParameters { get; init; } = [];

    public string? CreatedByLabel { get; init; }

    public DateTime CreatedTime { get; init; }

    public DateTime ExpiresTime { get; init; }

    public DateTime? RevokedTime { get; init; }

    public DateTime? LastTokenIssuedTime { get; init; }

    /// <summary>One of <see cref="EmbedGrantStatuses"/>.</summary>
    public string Status { get; init; } = null!;
}

/// <summary>Newest first unless <c>sort</c> says otherwise; optionally one project.</summary>
public sealed record ListEmbedGrantsQuery : ListRequest, IRequest<PagedList<EmbedGrantListItem>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? ProjectId { get; init; }
}
