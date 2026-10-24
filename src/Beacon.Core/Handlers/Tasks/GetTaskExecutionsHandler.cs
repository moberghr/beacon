using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.DTOs;
using Beacon.Core.Notifications;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskExecutionsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetTaskExecutionsQuery, TaskExecutionsResult>
{
    public async Task<TaskExecutionsResult> Handle(GetTaskExecutionsQuery request, CancellationToken cancellationToken)
    {
        // The executions are the stored runs the caller may read (StoredRunAccess).
        var executions = await Read(
            request.TaskId,
            await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken),
            cancellationToken);

        var items = executions
            .Select(x =>
                new TaskExecutionItem(
                    x.Id,
                    x.ExecutedAt,
                    x.ExecutionTimeMs,
                    x.ResultCount,
                    x.Status.ToString()))
            .ToList();

        return new TaskExecutionsResult(request.TaskId, items);
    }

    internal async Task<List<QueryExecutionSummary>> Read(int taskId, StoredRunScope runScope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscriptionId = await context.QueryTasks
            .Where(x => x.Id == taskId)
            .Select(x => x.SubscriptionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscriptionId == 0)
        {
            return [];
        }

        // The runs listed are the stored runs the caller may read (StoredRunAccess); the last 50.
        return await context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .WhereReadableWithin(context, runScope)
            .OrderByDescending(x => x.CreatedTime)
            .Take(50)
            .Select(x =>
                new QueryExecutionSummary(
                    x.Id,
                    x.CreatedTime,
                    x.ExecutionTimeMs,
                    x.NotificationStatus,
                    x.ResultCount))
            .ToListAsync(cancellationToken);
    }
}

public record GetTaskExecutionsQuery(int TaskId) : IRequest<TaskExecutionsResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Read)
    ];
}

public record TaskExecutionsResult(int TaskId, IReadOnlyList<TaskExecutionItem> Executions);

public record TaskExecutionItem(int Id, DateTime ExecutedAt, double DurationMs, int RowCount, string Status);
