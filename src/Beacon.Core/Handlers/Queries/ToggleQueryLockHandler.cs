using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Queries;

/// <summary>
/// Locks a query against AI and restore changes by anyone but its lock holder (it does not bind human edits), or
/// unlocks it, which only the lock holder or an Admin may do. A change of the lock is recorded as the
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

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
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
            // The id Beacon records people by, so the holder is recognised exactly when unlocking.
            query.LockedByUserId = actor.UserId ?? request.UserId;

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

            // The lock holder or an Admin lifts it; a lock with no recorded holder is lifted by whoever may manage the query.
            if (query.LockedByUserId != null && !actor.IsOwnerOrAdmin(query.LockedByUserId))
            {
                throw AccessRefusal.Of(logger, actor, "query", query.Id, "Only the user who locked the query or an Admin can unlock it.");
            }

            query.IsLocked = false;
            query.LockedAt = null;
            query.LockedByUserId = null;

            logger.LogInformation(
                "Query {QueryId} unlocked by user {UserId}",
                request.QueryId,
                request.UserId);
        }

        query.RecordChangeBy(actor, timeProvider);
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
        // Unlocking takes Manage, and the handler then lets only the lock holder or an Admin lift a held lock.
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
