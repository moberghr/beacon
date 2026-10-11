using Beacon.Core.Helpers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

public interface IQueryApprovalService
{
    /// <summary>
    /// The pending approval requests a caller may read (<paramref name="readable"/>): those whose query one readable
    /// project holds every source of, and whose version's recorded sources are readable the same way.
    /// </summary>
    Task<PagedList<ApprovalRequestSummary>> GetPendingApprovalsAsync(ListRequest request, int? queryId, ProjectSet readable, CancellationToken cancellationToken);
    Task<ApprovalRequestDetail?> GetApprovalDetailAsync(int requestId, CancellationToken cancellationToken = default);
    Task ApproveAsync(int requestId, string? reviewerUserId, string? reviewerName, string? comment, CancellationToken cancellationToken = default);
    Task RejectAsync(int requestId, string? reviewerUserId, string? reviewerName, string? comment, CancellationToken cancellationToken = default);
    Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default);
}

internal class QueryApprovalService(
    IDbContextFactory<BeaconContext> contextFactory,
    IQueryVersionService queryVersionService) : IQueryApprovalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PagedList<ApprovalRequestSummary>> GetPendingApprovalsAsync(
        ListRequest request,
        int? queryId,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Versions recorded before Beacon kept their sources are decided from their snapshots before paging, so the
        // page and its total count leave out the same unreadable requests.
        var unreadableLegacy = readable.Ids == null
            ? []
            : await UnreadableLegacyRequestIdsAsync(context, queryId, readable, cancellationToken);

        return await context.QueryApprovalRequests
            .Where(r => r.Status == ApprovalStatus.Pending)
            .Where(r => !queryId.HasValue || r.QueryId == queryId.Value)
            .WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId))
            .WhereVersionSourcesAccessible(context, readable, x => x.QueryVersion.DataSourceIds)
            .WhereIf(unreadableLegacy.Count > 0, x => !unreadableLegacy.Contains(x.Id))
            .Select(r => new ApprovalRequestSummary
            {
                Id = r.Id,
                QueryId = r.QueryId,
                QueryName = r.Query.Name,
                VersionNumber = r.QueryVersion.VersionNumber,
                Status = r.Status,
                RequestedByUserName = r.RequestedByUserName,
                CreatedTime = r.CreatedTime,
                ChangeSummary = r.ChangeSummary
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");
    }

    public async Task<ApprovalRequestDetail?> GetApprovalDetailAsync(int requestId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.QueryApprovalRequests
            .Include(r => r.Query)
            .Include(r => r.QueryVersion)
            .Where(r => r.Id == requestId)
            .SingleOrDefaultAsync(cancellationToken);

        if (request == null) return null;

        var proposedDetail = ToVersionDetail(request.QueryVersion);

        // Diff against the version the query points at; older data can mark several versions Active.
        var activeVersion = await context.QueryVersions
            .Where(v => v.Id == request.Query.ActiveVersionId)
            .FirstOrDefaultAsync(cancellationToken);

        QueryVersionDetail? activeDetail = null;
        QueryVersionDiff? autoDiff = null;

        if (activeVersion != null)
        {
            activeDetail = ToVersionDetail(activeVersion);
            autoDiff = await queryVersionService.DiffVersionsAsync(activeVersion.Id, request.QueryVersionId, cancellationToken);
        }

        return new ApprovalRequestDetail
        {
            Id = request.Id,
            QueryId = request.QueryId,
            QueryName = request.Query.Name,
            QueryVersionId = request.QueryVersionId,
            Status = request.Status,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByUserName = request.RequestedByUserName,
            ReviewedByUserName = request.ReviewedByUserName,
            ReviewedAt = request.ReviewedAt,
            ReviewComment = request.ReviewComment,
            ChangeSummary = request.ChangeSummary,
            CreatedTime = request.CreatedTime,
            ProposedVersion = proposedDetail,
            CurrentActiveVersion = activeDetail,
            AutoDiff = autoDiff
        };
    }

    public async Task ApproveAsync(int requestId, string? reviewerUserId, string? reviewerName, string? comment, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.QueryApprovalRequests
            .Include(r => r.QueryVersion)
            .Where(r => r.Id == requestId)
            .SingleAsync(cancellationToken);

        var query = await context.Queries
            .Include(q => q.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(q => q.Id == request.QueryId)
            .SingleAsync(cancellationToken);

        // Mark old active versions as Archived (older data can hold several)
        var currentActive = await context.QueryVersions
            .Where(v => v.QueryId == query.Id)
            .Where(v => v.Status == QueryVersionStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var x in currentActive)
        {
            x.Status = QueryVersionStatus.Archived;
        }

        // Apply snapshot from draft version to live query
        var snapshots = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(request.QueryVersion.StepsJson, JsonOptions) ?? [];

        query.Name = request.QueryVersion.Name;
        query.Description = request.QueryVersion.Description;
        query.FinalQuery = request.QueryVersion.FinalQuery;

        // Remove existing steps and parameters
        foreach (var step in query.Steps.ToList())
        {
            foreach (var param in step.Parameters.ToList())
            {
                context.QueryStepParameters.Remove(param);
            }
            context.QuerySteps.Remove(step);
        }

        await context.SaveChangesAsync(cancellationToken);

        // Recreate steps from snapshot
        foreach (var snapshot in snapshots)
        {
            var newStep = new QueryStep
            {
                QueryId = query.Id,
                StepOrder = snapshot.StepOrder,
                SqlValue = snapshot.SqlValue,
                DataSourceId = snapshot.DataSourceId,
                Name = snapshot.Name,
                Description = snapshot.Description
            };

            context.QuerySteps.Add(newStep);
            await context.SaveChangesAsync(cancellationToken);

            foreach (var paramSnapshot in snapshot.Parameters)
            {
                context.QueryStepParameters.Add(new QueryStepParameter
                {
                    QueryStepId = newStep.Id,
                    Name = paramSnapshot.Name,
                    Type = paramSnapshot.Type,
                    Description = paramSnapshot.Description,
                    Placeholder = paramSnapshot.Placeholder
                });
            }
        }

        // Mark draft version as Active
        request.QueryVersion.Status = QueryVersionStatus.Active;
        query.ActiveVersionId = request.QueryVersion.Id;

        // Mark approval request as Approved
        request.Status = ApprovalStatus.Approved;
        request.ReviewedByUserId = reviewerUserId;
        request.ReviewedByUserName = reviewerName;
        request.ReviewedAt = DateTime.UtcNow;
        request.ReviewComment = comment;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectAsync(int requestId, string? reviewerUserId, string? reviewerName, string? comment, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.QueryApprovalRequests
            .Include(r => r.QueryVersion)
            .Where(r => r.Id == requestId)
            .SingleAsync(cancellationToken);

        request.Status = ApprovalStatus.Rejected;
        request.ReviewedByUserId = reviewerUserId;
        request.ReviewedByUserName = reviewerName;
        request.ReviewedAt = DateTime.UtcNow;
        request.ReviewComment = comment;

        // Mark the draft version as rejected too
        request.QueryVersion.Status = QueryVersionStatus.Rejected;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.QueryApprovalRequests
            .CountAsync(r => r.Status == ApprovalStatus.Pending, cancellationToken);
    }

    private static QueryVersionDetail ToVersionDetail(QueryVersion version)
    {
        var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, JsonOptions) ?? [];

        return new QueryVersionDetail
        {
            Id = version.Id,
            VersionNumber = version.VersionNumber,
            Label = version.Label,
            Status = version.Status,
            Name = version.Name,
            Description = version.Description,
            FinalQuery = version.FinalQuery,
            CreatedTime = version.CreatedTime,
            ChangeSource = version.ChangeSource,
            ChangeReason = version.ChangeReason,
            Steps = steps
        };
    }

    // The pending requests (that the query filter keeps) whose version predates recorded sources and whose snapshot's
    // sources the caller may not read, or whose snapshot cannot be read: one read of the snapshots and at most one of
    // the links, whatever the number of requests.
    private static async Task<List<int>> UnreadableLegacyRequestIdsAsync(
        BeaconContext context,
        int? queryId,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        var legacy = await context.QueryApprovalRequests
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => !queryId.HasValue || x.QueryId == queryId.Value)
            .Where(x => x.QueryVersion.DataSourceIds == null)
            .WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId))
            .Select(x =>
                new
                {
                    x.Id,
                    x.QueryVersion.StepsJson
                })
            .ToListAsync(cancellationToken);
        if (legacy.Count == 0)
        {
            return [];
        }

        var sources = legacy
            .Select(x => QueryVersionSources.Of(null, x.StepsJson) ?? [])
            .ToList();
        var reachable = await ProjectSourceFootprint.AllowEachAsync(context, readable, sources, cancellationToken);

        return legacy
            .Where((_, i) => !reachable[i])
            .Select(x => x.Id)
            .ToList();
    }
}
