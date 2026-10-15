using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Queries;

/// <summary>
/// Locks a query against changes by anyone but its lock holder, or unlocks it. A change of the lock is recorded as the
/// query's last change (by the caller) and as a security audit event in the same save.
/// </summary>
internal sealed class ToggleQueryLockHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<ToggleQueryLockHandler> logger)
    : IRequestHandler<ToggleQueryLockCommand, ToggleQueryLockResult>
{
    public async Task<ToggleQueryLockResult> Handle(
        ToggleQueryLockCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Where(q => q.Id == request.QueryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (query == null)
        {
            return new ToggleQueryLockResult
            {
                Success = false,
                ErrorMessage = $"Query with ID {request.QueryId} not found"
            };
        }

        if (request.Lock)
        {
            if (query.IsLocked)
            {
                return new ToggleQueryLockResult
                {
                    Success = true,
                    QueryId = query.Id,
                    IsLocked = true,
                    Message = "Query is already locked"
                };
            }

            query.IsLocked = true;
            query.LockedAt = DateTime.UtcNow;
            query.LockedByUserId = request.UserId;

            logger.LogInformation(
                "Query {QueryId} locked by user {UserId}",
                request.QueryId,
                request.UserId);
        }
        else
        {
            if (!query.IsLocked)
            {
                return new ToggleQueryLockResult
                {
                    Success = true,
                    QueryId = query.Id,
                    IsLocked = false,
                    Message = "Query is already unlocked"
                };
            }

            query.IsLocked = false;
            query.LockedAt = null;
            query.LockedByUserId = null;

            logger.LogInformation(
                "Query {QueryId} unlocked by user {UserId}",
                request.QueryId,
                request.UserId);
        }

        query.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.QueryLockChanged)
                .On(SecurityAuditResources.Query, query.Id)
                .With(new SecurityAuditDetails().Add("locked", query.IsLocked)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return new ToggleQueryLockResult
        {
            Success = true,
            QueryId = query.Id,
            IsLocked = query.IsLocked,
            Message = query.IsLocked ? "Query locked successfully" : "Query unlocked successfully"
        };
    }
}

public record ToggleQueryLockCommand : IRequest<ToggleQueryLockResult>, IProjectAuthorizedRequest
{
    public required int QueryId { get; init; }
    public required bool Lock { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public string? UserId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        // Unlocking takes Manage; the lock holder, its recorded owner, may unlock wherever it may write.
        new(ProjectResource.Query(QueryId), Lock ? ProjectAction.Write : ProjectAction.Manage)
    ];
}

public record ToggleQueryLockResult
{
    public bool Success { get; init; }
    public int QueryId { get; init; }
    public bool IsLocked { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }
}
