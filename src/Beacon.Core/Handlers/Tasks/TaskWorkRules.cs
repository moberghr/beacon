using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Tasks;

/// <summary>
/// Who may work an alert task. Resolving, snoozing and setting the priority belong to the task's current assignee or
/// an Admin, and are written only while the task is open and still has the assignee the check saw. Anyone with write
/// permission (the permission filter's write check) may claim an unassigned task by assigning it to themselves; the
/// assignee may release the task or hand it over; only an Admin assigns an unassigned task to someone else or
/// reassigns, or re-confirms, a task assigned to someone else. The caller is the
/// <see cref="BeaconActor"/> of the request, identified by the <c>Users.ExternalId</c> the task stores (or, for a
/// task assigned by an earlier version, by an id its session presents). To anyone but an Admin, a task that does not
/// exist is refused like a task that is not theirs.
/// </summary>
internal static class TaskWorkRules
{
    private const string Resource = "task";

    public static void EnsureCanWork(BeaconActor actor, int taskId, string? assigneeUserId, string action, ILogger logger)
    {
        if (actor.IsAdmin || actor.IsAssignee(assigneeUserId))
        {
            return;
        }

        throw AccessRefusal.Of(logger, actor, Resource, taskId, $"Only the task's assignee or an Admin can {action} this task.");
    }

    public static void EnsureCanAssign(
        BeaconActor actor,
        int taskId,
        string? currentAssigneeUserId,
        string? assigneeUserId,
        ILogger logger)
    {
        // Unassigning an unassigned task changes nothing; it is allowed to a caller with a user, like a claim.
        var claimsUnassignedTask = currentAssigneeUserId == null && actor.Is(assigneeUserId);
        var leavesUnassignedTask = currentAssigneeUserId == null && assigneeUserId == null && actor.UserId != null;
        if (actor.IsAdmin || actor.IsAssignee(currentAssigneeUserId) || claimsUnassignedTask || leavesUnassignedTask)
        {
            return;
        }

        throw AccessRefusal.Of(
            logger,
            actor,
            Resource,
            taskId,
            "Only an Admin can assign a task to someone else or reassign a task that is assigned to someone else.");
    }

    /// <summary>A task that does not exist: not found for an Admin; for anyone else, the refusal a task not theirs gets.</summary>
    public static Exception Missing(BeaconActor actor, int taskId, string action, ILogger logger)
    {
        if (actor.IsAdmin)
        {
            return new InvalidOperationException($"Task {taskId} not found.");
        }

        return AccessRefusal.Of(logger, actor, Resource, taskId, $"Only the task's assignee or an Admin can {action} this task.");
    }

    /// <summary>
    /// The assignee a request names, null to unassign. A value that is one of the caller's own ids (the id
    /// <c>/auth/me</c> reports, the <c>NameIdentifier</c>) names the caller, and is stored as the caller's
    /// <see cref="BeaconActor.UserId"/>.
    /// </summary>
    public static string? AssigneeOf(string? requested, BeaconActor actor)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        return actor.UserId != null && actor.SessionIds.Contains(requested) ? actor.UserId : requested;
    }
}
