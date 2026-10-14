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
            details.AssigneeUserId != null,
            details.AssigneeUserName,
            actor.IsAssignee(details.AssigneeUserId),
            details.SnoozedUntil,
            details.SlaHours,
            details.WatcherCount,
            details.IsWatching,
            details.HasOwner,
            details.OwnerDisplayName);
    }
}

public record GetTaskDetailQuery(int Id) : IRequest<TaskDetailResult?>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(Id), ProjectAction.Read)
    ];
}

/// <summary>
/// A task's detail. <c>HasAssignee</c> says whether the task is assigned, <c>AssigneeUserName</c> names the assignee
/// (display name or "User {id}"; null when the assignee is not a Beacon user) and <c>AssignedToCaller</c> says whether the
/// caller is the task's assignee, the check resolving, snoozing, setting the priority and releasing the task are made
/// against (besides the Admin role); the assignee's external id is not returned. <c>HasOwner</c>: the
/// task's subscription is managed by an AI actor with a recorded creator; <c>OwnerDisplayName</c>: that creator's
/// display name, or a neutral "User {id}" label, never an external id or user name.
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
    bool HasAssignee,
    string? AssigneeUserName,
    bool AssignedToCaller,
    DateTime? SnoozedUntil,
    int? SlaHours,
    int WatcherCount,
    bool IsWatching,
    bool HasOwner,
    string? OwnerDisplayName);
