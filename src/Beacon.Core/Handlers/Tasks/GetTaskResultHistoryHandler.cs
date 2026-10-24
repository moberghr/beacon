using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.DTOs;
using Beacon.Core.Notifications;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskResultHistoryHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetTaskResultHistoryQuery, TaskResultHistoryResult>
{
    public async Task<TaskResultHistoryResult> Handle(GetTaskResultHistoryQuery request, CancellationToken cancellationToken)
    {
        // The points are the result counts of the stored runs the caller may read (StoredRunAccess).
        var history = await Read(
            request.TaskId,
            await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken),
            cancellationToken);

        var items = history
            .Select(x => new TaskResultHistoryItem(x.Date, x.ResultCount))
            .ToList();

        return new TaskResultHistoryResult(request.TaskId, items);
    }

    internal async Task<List<ResultCountDataPoint>> Read(int taskId, StoredRunScope runScope, CancellationToken cancellationToken)
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

        // The most recent 100 result counts of the stored runs the caller may read (StoredRunAccess), re-ordered
        // ascending for the chart.
        var history = await context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .WhereReadableWithin(context, runScope)
            .OrderByDescending(x => x.CreatedTime)
            .Take(100)
            .Select(x => new ResultCountDataPoint(x.CreatedTime, x.ResultCount))
            .ToListAsync(cancellationToken);

        history.Reverse();

        return history;
    }
}

public record GetTaskResultHistoryQuery(int TaskId) : IRequest<TaskResultHistoryResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Read)
    ];
}

public record TaskResultHistoryResult(int TaskId, IReadOnlyList<TaskResultHistoryItem> Points);

public record TaskResultHistoryItem(DateTime SampledAt, int ResultCount);
