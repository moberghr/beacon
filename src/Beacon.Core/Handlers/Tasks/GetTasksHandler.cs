using Beacon.Core.Helpers;
using Beacon.Core.Models.Tasks;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTasksHandler(ITaskService taskService, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetTasksQuery, PagedList<TaskEntry>>
{
    public async Task<PagedList<TaskEntry>> Handle(GetTasksQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var serviceRequest = new GetTasksRequest
        {
            Readable = scope.For(ProjectAction.Read),
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            SubscriptionId = request.SubscriptionId,
            Resolved = request.Resolved,
        };

        var result = await taskService.GetTasks(serviceRequest, cancellationToken);

        return result.Map(x =>
            new TaskEntry(
                x.Id,
                x.SubscriptionName,
                x.QueryName,
                x.LatestResultCount,
                x.NotificationCount,
                x.ExecutionCount,
                x.UniqueResultCounts,
                x.CreatedAt,
                x.Resolved,
                x.ResolvedAt,
                x.ResolvedByUserName,
                x.AiActorId,
                x.AiActorName));
    }
}

public record GetTasksQuery : ListRequest, IRequest<PagedList<TaskEntry>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? SubscriptionId { get; init; }

    public bool? Resolved { get; init; }
}

public record TaskEntry(
    int Id,
    string SubscriptionName,
    string QueryName,
    int LatestResultCount,
    int NotificationCount,
    int ExecutionCount,
    int UniqueResultCounts,
    DateTime CreatedAt,
    bool Resolved,
    DateTime? ResolvedAt,
    string? ResolvedByUserName,
    int? AiActorId,
    string? AiActorName);
