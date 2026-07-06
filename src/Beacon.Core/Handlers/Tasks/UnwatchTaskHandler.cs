using Beacon.Core.Authorization;
using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class UnwatchTaskHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext)
    : IRequestHandler<UnwatchTaskCommand>
{
    public async Task Handle(UnwatchTaskCommand request, CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrEmpty(userContext.UserId))
        {
            throw new InvalidOperationException("Authenticated user required to unwatch a task.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var userId = userContext.UserId;

        var watcher = await context.TaskWatchers
            .Where(x => x.QueryTaskId == request.TaskId)
            .Where(x => x.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        if (watcher == null)
        {
            return;
        }

        context.TaskWatchers.Remove(watcher);
        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UnwatchTaskCommand(int TaskId) : IRequest;
