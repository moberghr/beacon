using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class SetTaskPriorityHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<SetTaskPriorityHandler> logger)
    : IRequestHandler<SetTaskPriorityCommand>
{
    public async Task Handle(SetTaskPriorityCommand request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(typeof(TaskPriority), request.Priority))
        {
            throw new InvalidOperationException($"Invalid priority value: {request.Priority}.");
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
            ?? throw TaskWorkRules.Missing(actor, request.TaskId, "change the priority of", logger);

        TaskWorkRules.EnsureCanWork(actor, request.TaskId, task.AssigneeUserId, "change the priority of", logger);

        if (task.Resolved)
        {
            throw new InvalidOperationException($"Task {request.TaskId} is resolved: only an open task can have its priority changed.");
        }

        // Written only while the task is still open and still has the assignee the check above saw.
        var assigneeUserId = task.AssigneeUserId;
        var updated = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .Where(x => !x.Resolved)
            .Where(x => x.AssigneeUserId == assigneeUserId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Priority, request.Priority), cancellationToken);

        if (updated == 0)
        {
            throw new InvalidOperationException($"Task {request.TaskId} was resolved or reassigned meanwhile.");
        }
    }
}

/// <summary>Sets an open task's priority. Allowed for the task's assignee or an Admin; a resolved task keeps its priority.</summary>
public record SetTaskPriorityCommand(int TaskId, TaskPriority Priority) : IRequest;
