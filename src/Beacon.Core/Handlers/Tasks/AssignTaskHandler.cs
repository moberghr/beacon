using Beacon.Core.Authorization;
using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class AssignTaskHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration,
    ILogger<AssignTaskHandler> logger)
    : IRequestHandler<AssignTaskCommand>
{
    public async Task Handle(AssignTaskCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .Select(x =>
                new
                {
                    x.AssigneeUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw TaskWorkRules.Missing(actor, request.TaskId, "assign", logger);

        var currentAssigneeUserId = task.AssigneeUserId;
        var assigneeUserId = TaskWorkRules.AssigneeOf(request.AssigneeUserId, actor);
        TaskWorkRules.EnsureCanAssign(actor, request.TaskId, currentAssigneeUserId, assigneeUserId, logger);

        if (assigneeUserId == currentAssigneeUserId)
        {
            return;
        }

        if (assigneeUserId != null && !await AssignableUsers.IsAssignableAsync(context, configuration, assigneeUserId, cancellationToken))
        {
            throw new InvalidOperationException("A task can only be assigned to an existing, enabled user.");
        }

        // Written only while the task still has the assignee the check above saw.
        var updated = await context.QueryTasks
            .Where(x => x.Id == request.TaskId)
            .Where(x => x.AssigneeUserId == currentAssigneeUserId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.AssigneeUserId, assigneeUserId), cancellationToken);

        if (updated == 0)
        {
            throw new InvalidOperationException($"Task {request.TaskId} was reassigned meanwhile.");
        }
    }
}

/// <summary>
/// Assigns a task (<c>AssigneeUserId</c> is a user's external id; null or blank unassigns). Anyone with write
/// permission may claim an unassigned task for themselves; the assignee may release or hand over their task; anything
/// else needs an Admin. With Beacon user management the assignee must be an existing, enabled user.
/// </summary>
public record AssignTaskCommand(int TaskId, string? AssigneeUserId) : IRequest;
