using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.DTOs;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Tasks;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskDetailHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext,
    IBeaconActorAccessor actorAccessor)
    : IRequestHandler<GetTaskDetailQuery, TaskDetailResult?>
{
    public async Task<TaskDetailResult?> Handle(GetTaskDetailQuery request, CancellationToken cancellationToken)
    {
        var currentUserId = userContext.IsAuthenticated ? userContext.UserId : null;

        var details = await Read(request.Id, currentUserId, cancellationToken);

        if (details == null)
        {
            return null;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        return ToResult(details, actor);
    }

    // The detail as the caller sees it: the assignee's external id is reduced to "assigned" and "assigned to the caller".
    internal static TaskDetailResult ToResult(TaskDetailsData details, BeaconActor actor)
    {
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

    internal async Task<TaskDetailsData?> Read(int taskId, string? currentUserId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.QueryTasks
            .Where(x => x.Id == taskId)
            .Select(x =>
                new TaskDetailsData
                {
                    Id = x.Id,
                    Subscription = new SubscriptionSummary(
                        x.Subscription.Id,
                        x.Subscription.Query.Name,
                        null),
                    LatestResultCount = x.LatestResultCount,
                    LastNotificationAt = x.LastNotificationAt,
                    NotificationCount = x.Notifications.Count,
                    // The notifications' stored result rows are not part of a task's detail.
                    Notifications = x.Notifications
                        .OrderByDescending(y => y.SentAt)
                        .Select(y =>
                            new NotificationSummary(
                                y.Id,
                                y.SentAt,
                                y.QueryExecutionHistory.ResultCount))
                        .ToList(),
                    CreatedAt = x.CreatedTime,
                    Resolved = x.Resolved,
                    ResolvedAt = x.ResolvedAt,
                    ResolvedByUserId = x.ResolvedByUserId,
                    // Named by display name or "User {id}", never by user name.
                    ResolvedByUserName = x.ResolvedByUserId == null
                        ? null
                        : context.Users
                            .Where(y => y.ExternalId == x.ResolvedByUserId)
                            .Select(UserLabel.OfUser)
                            .FirstOrDefault(),
                    ResolutionNotes = x.ResolutionNotes,
                    QueryId = x.Subscription.QueryId,
                    QueryName = x.Subscription.Query.Name,
                    AiActorId = x.Subscription.AiActorId,
                    AiActorName = x.Subscription.AiActor != null ? x.Subscription.AiActor.Name : null,
                    CronExpression = x.Subscription.CronExpression,
                    LastExecutionAt = context.QueryExecutionHistory
                        .Where(y => y.SubscriptionId == x.SubscriptionId)
                        .OrderByDescending(y => y.CreatedTime)
                        .Select(y => (DateTime?)y.CreatedTime)
                        .FirstOrDefault(),
                    Priority = x.Priority,
                    AssigneeUserId = x.AssigneeUserId,
                    AssigneeUserName = x.AssigneeUserId == null
                        ? null
                        : context.Users
                            .Where(y => y.ExternalId == x.AssigneeUserId)
                            .Select(UserLabel.OfUser)
                            .FirstOrDefault(),
                    SnoozedUntil = x.SnoozedUntil,
                    SlaHours = x.Subscription.SlaHours,
                    WatcherCount = x.Watchers.Count,
                    IsWatching = currentUserId != null && x.Watchers.Any(y => y.UserId == currentUserId),
                    HasOwner = x.Subscription.AiActor != null && x.Subscription.AiActor.CreatedByUserId != null,
                    OwnerDisplayName = x.Subscription.AiActor != null && x.Subscription.AiActor.CreatedByUserId != null
                        ? context.Users
                            .Where(y => y.ExternalId == x.Subscription.AiActor!.CreatedByUserId)
                            .Select(UserLabel.OfUser)
                            .FirstOrDefault()
                        : null
                })
            .FirstOrDefaultAsync(cancellationToken);
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
