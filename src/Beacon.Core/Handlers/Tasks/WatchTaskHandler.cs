using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class WatchTaskHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext)
    : IRequestHandler<WatchTaskCommand>
{
    public async Task Handle(WatchTaskCommand request, CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrEmpty(userContext.UserId))
        {
            throw new InvalidOperationException("Authenticated user required to watch a task.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var taskExists = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .AnyAsync(cancellationToken);

        if (!taskExists)
        {
            throw new InvalidOperationException($"Task {request.TaskId} not found.");
        }

        var userId = userContext.UserId;

        var alreadyWatching = await context.TaskWatchers
            .Where(x => x.QueryTaskId == request.TaskId)
            .Where(x => x.UserId == userId)
            .AnyAsync(cancellationToken);

        if (alreadyWatching)
        {
            return;
        }

        context.TaskWatchers.Add(new TaskWatcher
        {
            QueryTaskId = request.TaskId,
            UserId = userId,
            CreatedTime = DateTime.UtcNow
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record WatchTaskCommand(int TaskId) : IRequest;
