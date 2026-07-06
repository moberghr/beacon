using Beacon.Core.Helpers;
using Beacon.Core.Models.Tasks;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTasksHandler(ITaskService taskService)
    : IRequestHandler<GetTasksQuery, GetTasksResult>
{
    public async Task<GetTasksResult> Handle(GetTasksQuery request, CancellationToken cancellationToken)
    {
        var serviceRequest = new GetTasksRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            SubscriptionId = request.SubscriptionId,
            Resolved = request.Resolved,
            SortCriteria = string.IsNullOrWhiteSpace(request.SortColumn)
                ? new List<SortCriterion>()
                : new List<SortCriterion>
                {
                    new(request.SortColumn, request.SortDescending ? SortDirection.Descending : SortDirection.Ascending),
                },
        };

        var result = await taskService.GetTasks(serviceRequest, cancellationToken);

        var entries = result.Data
            .Select(x =>
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
                    x.AiActorName))
            .ToList();

        return new GetTasksResult(entries, result.TotalCount ?? entries.Count);
    }
}

public record GetTasksQuery(
    int Page,
    int PageSize,
    int? SubscriptionId,
    bool? Resolved,
    string? SortColumn,
    bool SortDescending) : IRequest<GetTasksResult>;

public record GetTasksResult(List<TaskEntry> Entries, int TotalCount);

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
