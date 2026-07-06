using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class SetTaskPriorityHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<SetTaskPriorityCommand>
{
    public async Task Handle(SetTaskPriorityCommand request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(typeof(TaskPriority), request.Priority))
        {
            throw new InvalidOperationException($"Invalid priority value: {request.Priority}.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new InvalidOperationException($"Task {request.TaskId} not found.");
        }

        task.Priority = request.Priority;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record SetTaskPriorityCommand(int TaskId, TaskPriority Priority) : IRequest;
