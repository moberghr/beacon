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
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw TaskWorkRules.Missing(actor, request.TaskId, "change the priority of", logger);

        TaskWorkRules.EnsureCanWork(actor, task.Id, task.AssigneeUserId, "change the priority of", logger);

        task.Priority = request.Priority;

        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Sets a task's priority. Allowed for the task's assignee or an Admin.</summary>
public record SetTaskPriorityCommand(int TaskId, TaskPriority Priority) : IRequest;
