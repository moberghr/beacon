using Beacon.Core.Authorization;
using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class ResolveTaskHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<ResolveTaskHandler> logger)
    : IRequestHandler<ResolveTaskCommand>
{
    public async Task Handle(ResolveTaskCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .Where(x => x.Id == request.Id)
            .Select(x =>
                new
                {
                    x.AssigneeUserId,
                    x.Resolved
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw TaskWorkRules.Missing(actor, request.Id, "resolve", logger);

        TaskWorkRules.EnsureCanWork(actor, request.Id, task.AssigneeUserId, "resolve", logger);

        // The resolver is recorded as the caller, and a resolution without one is not recorded.
        var resolvedByUserId = actor.UserId
            ?? throw new InvalidOperationException("The caller has no resolvable user to record as the task's resolver.");

        if (task.Resolved)
        {
            throw new InvalidOperationException($"Task {request.Id} is already resolved.");
        }

        // Written only while the task is still open and still has the assignee the check above saw: a resolution is
        // never overwritten.
        var assigneeUserId = task.AssigneeUserId;
        var resolvedAt = DateTime.UtcNow;
        var updated = await context.QueryTasks
            .Where(x => x.Id == request.Id)
            .Where(x => !x.Resolved)
            .Where(x => x.AssigneeUserId == assigneeUserId)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Resolved, true)
                    .SetProperty(y => y.ResolvedAt, resolvedAt)
                    .SetProperty(y => y.ResolutionNotes, request.ResolutionNotes)
                    .SetProperty(y => y.ResolvedByUserId, resolvedByUserId),
                cancellationToken);

        if (updated == 0)
        {
            throw new InvalidOperationException($"Task {request.Id} is already resolved or was reassigned meanwhile.");
        }
    }
}

/// <summary>
/// Resolves an open task. Allowed for the task's assignee or an Admin; the resolver is recorded as the signed-in caller.
/// Resolving a task that is already resolved fails.
/// </summary>
public record ResolveTaskCommand(int Id, string? ResolutionNotes) : IRequest;
