using Beacon.Core.Helpers;
using Beacon.Core.Models.Tasks;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTasksHandler(ITaskService taskService)
    : IRequestHandler<GetTasksQuery, PagedList<TaskEntry>>
{
    public async Task<PagedList<TaskEntry>> Handle(GetTasksQuery request, CancellationToken cancellationToken)
    {
        var serviceRequest = new GetTasksRequest
        {
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

public record GetTasksQuery : ListRequest, IRequest<PagedList<TaskEntry>>
{
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
