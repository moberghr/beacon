using Beacon.Core.Authorization;
using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class SnoozeTaskHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<SnoozeTaskHandler> logger)
    : IRequestHandler<SnoozeTaskCommand>
{
    public async Task Handle(SnoozeTaskCommand request, CancellationToken cancellationToken)
    {
        if (request.SnoozeUntil.HasValue && request.SnoozeUntil.Value <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Snooze time must be in the future.");
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .Select(x =>
                new
                {
                    x.AssigneeUserId,
                    x.Resolved
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw TaskWorkRules.Missing(actor, request.TaskId, "snooze", logger);

        TaskWorkRules.EnsureCanWork(actor, request.TaskId, task.AssigneeUserId, "snooze", logger);

        if (task.Resolved)
        {
            throw new InvalidOperationException($"Task {request.TaskId} is resolved: only an open task can be snoozed.");
        }

        // Written only while the task is still open and still has the assignee the check above saw.
        var assigneeUserId = task.AssigneeUserId;
        var updated = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .Where(x => !x.Resolved)
            .Where(x => x.AssigneeUserId == assigneeUserId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.SnoozedUntil, request.SnoozeUntil), cancellationToken);

        if (updated == 0)
        {
            throw new InvalidOperationException($"Task {request.TaskId} was resolved or reassigned meanwhile.");
        }
    }
}

/// <summary>
/// Snoozes an open task until the given time, or wakes it with none. Allowed for the task's assignee or an Admin;
/// a resolved task is not snoozed.
/// </summary>
public record SnoozeTaskCommand(int TaskId, DateTime? SnoozeUntil) : IRequest;
