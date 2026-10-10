using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Models;

namespace Beacon.Core.Authorization;

/// <summary>
/// Refused access, logged with ids only (§1.11): the resource, the caller's principal kind, its API key and the one
/// actor id (<see cref="BeaconActor.LogId"/>). A refused action on a resource the caller may see is a 403
/// (<see cref="Of"/>); a resource it may not see, or one that is missing, answers 404 like a missing one
/// (<see cref="Hidden"/>), and is logged as well so a probe for other projects' resources leaves a trace. Public so
/// every Beacon assembly logs refusals the same way.
/// </summary>
public static class AccessRefusal
{
    /// <summary>The resource exists, but the caller may not read it.</summary>
    public const string NotReadable = "not-readable";

    /// <summary>The resource does not exist (or is archived).</summary>
    public const string Missing = "missing";

    /// <summary>The caller may read the resource, but the action is not granted to it.</summary>
    public const string NotGranted = "not-granted";

    public static BeaconForbiddenException Of(
        ILogger logger,
        BeaconActor actor,
        string resource,
        int? resourceId,
        string message)
    {
        logger.LogWarning(
            ProjectAccessEvents.AccessRefused,
            "Access refused: {Resource} {ResourceId}, reason {Reason}, principal {PrincipalKind}, API key {ApiKeyId}, user {ActorId}",
            resource,
            resourceId,
            NotGranted,
            actor.Kind,
            actor.ApiKeyId,
            actor.LogId);

        return new BeaconForbiddenException(message);
    }

    /// <summary>
    /// Logs a resource answered as not found for <paramref name="action"/>: at Warning when it exists but the caller may
    /// not read it, at Information when it is missing. The caller throws the 404.
    /// </summary>
    public static void Hidden(
        ILogger logger,
        BeaconActor actor,
        string resource,
        int? resourceId,
        ProjectAction action,
        string reason)
    {
        logger.Log(
            reason == Missing ? LogLevel.Information : LogLevel.Warning,
            ProjectAccessEvents.AccessHidden,
            "Access answered as not found: {Resource} {ResourceId}, action {Action}, reason {Reason}, principal {PrincipalKind}, API key {ApiKeyId}, user {ActorId}",
            resource,
            resourceId,
            action,
            reason,
            actor.Kind,
            actor.ApiKeyId,
            actor.LogId);
    }
}
