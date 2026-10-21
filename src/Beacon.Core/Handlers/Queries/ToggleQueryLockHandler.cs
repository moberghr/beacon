using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.Models;
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
        var observed = await context.Queries
            .Where(x => x.Id == request.QueryId)
            .Select(x =>
                new
                {
                    x.IsLocked,
                    x.LockedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (observed == null)
        {
            return new ToggleQueryLockResult
            {
                Success = false,
                ErrorMessage = $"Query with ID {request.QueryId} not found"
            };
        }

        if (request.Lock == observed.IsLocked)
        {
            return new ToggleQueryLockResult
            {
                Success = true,
                QueryId = request.QueryId,
                IsLocked = observed.IsLocked,
                Message = observed.IsLocked ? "Query is already locked" : "Query is already unlocked"
            };
        }

        // The lock holder or an Admin lifts it; a lock with no recorded holder is lifted by whoever may manage the query.
        if (!request.Lock && observed.LockedByUserId != null && !actor.IsOwnerOrAdmin(observed.LockedByUserId))
        {
            throw AccessRefusal.Of(logger, actor, "query", request.QueryId, "Only the user who locked the query or an Admin can unlock it.");
        }

        // Written only while the lock is still in the state the checks above saw, with its security audit event and the
        // attribution of the change: a lock or an unlock that lost a race to another one changes nothing.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var holder = actor.UserId ?? request.UserId;
        var observedHolder = observed.LockedByUserId;
        var written = await securityAudit.SaveWithEventsAsync(
            context,
            () => request.Lock
                ? context.Queries
                    .Where(x => x.Id == request.QueryId)
                    .Where(x => !x.IsLocked)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(y => y.IsLocked, true)
                            .SetProperty(y => y.LockedAt, now)
                            .SetProperty(y => y.LockedByUserId, holder)
                            .SetProperty(y => y.ModifiedByUserId, actor.UserId)
                            .SetProperty(y => y.ModifiedTime, now),
                        cancellationToken)
                : context.Queries
                    .Where(x => x.Id == request.QueryId)
                    .Where(x => x.IsLocked)
                    .Where(x => x.LockedByUserId == observedHolder)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(y => y.IsLocked, false)
                            .SetProperty(y => y.LockedAt, (DateTime?)null)
                            .SetProperty(y => y.LockedByUserId, (string?)null)
                            .SetProperty(y => y.ModifiedByUserId, actor.UserId)
                            .SetProperty(y => y.ModifiedTime, now),
                        cancellationToken),
            changed => changed == 0
                ? []
                :
                [
                    new SecurityAuditEntry(SecurityAuditActions.QueryLockChanged)
                        .On(SecurityAuditResources.Query, request.QueryId)
                        .With(new SecurityAuditDetails().Add("locked", request.Lock))
                ],
            cancellationToken);

        if (written == 0)
        {
            throw new BeaconConflictException("The query's lock changed meanwhile; reload and retry.");
        }

        logger.LogInformation(
            "Query {QueryId} {Change} by user {UserId}",
            request.QueryId,
            request.Lock ? "locked" : "unlocked",
            request.UserId);

        return new ToggleQueryLockResult
        {
            Success = true,
            QueryId = request.QueryId,
            IsLocked = request.Lock,
            Message = request.Lock ? "Query locked successfully" : "Query unlocked successfully"
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
