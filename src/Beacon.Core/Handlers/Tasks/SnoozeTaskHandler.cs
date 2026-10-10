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
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw TaskWorkRules.Missing(actor, request.TaskId, "snooze", logger);

        TaskWorkRules.EnsureCanWork(actor, task.Id, task.AssigneeUserId, "snooze", logger);

        task.SnoozedUntil = request.SnoozeUntil;

        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Snoozes a task until the given time, or wakes it with none. Allowed for the task's assignee or an Admin.</summary>
public record SnoozeTaskCommand(int TaskId, DateTime? SnoozeUntil) : IRequest;
