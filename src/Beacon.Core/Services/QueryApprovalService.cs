using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

public interface IQueryApprovalService
{
    Task<List<ApprovalRequestSummary>> GetPendingApprovalsAsync(int? queryId = null, CancellationToken cancellationToken = default);
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

    public async Task<List<ApprovalRequestSummary>> GetPendingApprovalsAsync(int? queryId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.QueryApprovalRequests
            .Where(r => r.Status == ApprovalStatus.Pending)
            .Where(r => !queryId.HasValue || r.QueryId == queryId.Value)
            .OrderByDescending(r => r.CreatedTime)
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
            .ToListAsync(cancellationToken);
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

        // Get current active version for diff
        var activeVersion = await context.QueryVersions
            .Where(v => v.QueryId == request.QueryId && v.Status == QueryVersionStatus.Active)
            .SingleOrDefaultAsync(cancellationToken);

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

        // Mark old active version as Archived
        var currentActive = await context.QueryVersions
            .Where(v => v.QueryId == query.Id && v.Status == QueryVersionStatus.Active)
            .SingleOrDefaultAsync(cancellationToken);

        if (currentActive != null)
        {
            currentActive.Status = QueryVersionStatus.Archived;
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
            CreatedByUserId = version.CreatedByUserId,
            ChangeSource = version.ChangeSource,
            ChangeReason = version.ChangeReason,
            Steps = steps
        };
    }
}
