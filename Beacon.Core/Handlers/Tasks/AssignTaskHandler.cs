using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class AssignTaskHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<AssignTaskCommand>
{
    public async Task Handle(AssignTaskCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new InvalidOperationException($"Task {request.TaskId} not found.");
        }

        task.AssigneeUserId = string.IsNullOrWhiteSpace(request.AssigneeUserId)
            ? null
            : request.AssigneeUserId;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record AssignTaskCommand(int TaskId, string? AssigneeUserId) : IRequest;
