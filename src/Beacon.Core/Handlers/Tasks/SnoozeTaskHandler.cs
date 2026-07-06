using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class SnoozeTaskHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<SnoozeTaskCommand>
{
    public async Task Handle(SnoozeTaskCommand request, CancellationToken cancellationToken)
    {
        if (request.SnoozeUntil.HasValue && request.SnoozeUntil.Value <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Snooze time must be in the future.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new InvalidOperationException($"Task {request.TaskId} not found.");
        }

        task.SnoozedUntil = request.SnoozeUntil;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record SnoozeTaskCommand(int TaskId, DateTime? SnoozeUntil) : IRequest;
