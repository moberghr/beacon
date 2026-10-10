using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskDetailHandler(
    ITaskService taskService,
    IBeaconUserContext userContext,
    IBeaconActorAccessor actorAccessor)
    : IRequestHandler<GetTaskDetailQuery, TaskDetailResult?>
{
    public async Task<TaskDetailResult?> Handle(GetTaskDetailQuery request, CancellationToken cancellationToken)
    {
        var currentUserId = userContext.IsAuthenticated ? userContext.UserId : null;

        var details = await taskService.GetTaskDetails(request.Id, currentUserId, cancellationToken);

        if (details == null)
        {
            return null;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        return new TaskDetailResult(
            details.Id,
            details.QueryId,
            details.QueryName,
            details.Subscription.Id,
            details.Subscription.Name,
            details.Subscription.Description,
            details.LatestResultCount,
            details.NotificationCount,
            details.LastNotificationAt,
            details.CreatedAt,
            details.Resolved,
            details.ResolvedAt,
            details.ResolvedByUserName,
            details.ResolutionNotes,
            details.AiActorId,
            details.AiActorName,
            details.LastExecutionAt,
            details.CronExpression,
            details.Priority,
            details.AssigneeUserId,
            details.AssigneeUserName,
            actor.IsAssignee(details.AssigneeUserId),
            details.SnoozedUntil,
            details.SlaHours,
            details.WatcherCount,
            details.IsWatching,
            details.OwnerUserId,
            details.OwnerUserName);
    }
}

public record GetTaskDetailQuery(int Id) : IRequest<TaskDetailResult?>;

/// <summary>
/// A task's detail. <c>AssignedToCaller</c> says whether the caller is the task's assignee, the check resolving,
/// snoozing, setting the priority and releasing the task are made against (besides the Admin role).
/// </summary>
public record TaskDetailResult(
    int Id,
    int QueryId,
    string QueryName,
    int SubscriptionId,
    string SubscriptionName,
    string? SubscriptionDescription,
    int LatestResultCount,
    int NotificationCount,
    DateTime? LastNotificationAt,
    DateTime CreatedAt,
    bool Resolved,
    DateTime? ResolvedAt,
    string? ResolvedByUserName,
    string? ResolutionNotes,
    int? AiActorId,
    string? AiActorName,
    DateTime? LastExecutionAt,
    string? CronExpression,
    TaskPriority Priority,
    string? AssigneeUserId,
    string? AssigneeUserName,
    bool AssignedToCaller,
    DateTime? SnoozedUntil,
    int? SlaHours,
    int WatcherCount,
    bool IsWatching,
    string? OwnerUserId,
    string? OwnerUserName);
