using Microsoft.Extensions.Logging;
using Beacon.Core.Models;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The answer to a refused project check: 404 when the caller may not read the project (as for a missing one), 403
/// when it may read it but not take the action. Both are logged with ids only.
/// </summary>
internal static class ProjectAccessGuard
{
    public static void Demand(
        ProjectAccessScope scope,
        int projectId,
        ProjectAction action,
        BeaconActor actor,
        ILogger logger)
    {
        if (!scope.Allows(projectId, ProjectAction.Read))
        {
            throw NotFound(logger, actor, projectId, action, AccessRefusal.NotReadable);
        }

        if (!scope.Allows(projectId, action))
        {
            throw AccessRefusal.Of(logger, actor, $"project ({action})", projectId, $"{Describe(action)} is not allowed in this project.");
        }
    }

    public static BeaconNotFoundException NotFound(int projectId)
    {
        return new BeaconNotFoundException($"Project {projectId} not found.");
    }

    /// <summary>The 404 for <paramref name="projectId"/>, logged with why (<see cref="AccessRefusal"/>).</summary>
    public static BeaconNotFoundException NotFound(
        ILogger logger,
        BeaconActor actor,
        int projectId,
        ProjectAction action,
        string reason)
    {
        AccessRefusal.Hidden(logger, actor, nameof(ProjectResourceKind.Project), projectId, action, reason);

        return NotFound(projectId);
    }

    private static string Describe(ProjectAction action)
    {
        return action switch
        {
            ProjectAction.ManageMembers => "Managing members and data sources",
            _ => action.ToString()
        };
    }
}
