using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.Users;

internal sealed class SignUserOutEverywhereHandler(
    UserAdministrationRules rules,
    IUserSessionStore sessions,
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration,
    ILogger<SignUserOutEverywhereHandler> logger)
    : IRequestHandler<SignUserOutEverywhereCommand>
{
    public async Task Handle(SignUserOutEverywhereCommand request, CancellationToken cancellationToken)
    {
        // Sessions are checked against the user store only while Beacon user management is on; without it a new security
        // stamp would end nothing, so nothing is changed and the caller is told.
        if (!configuration.UserManagement.Enabled)
        {
            throw new BeaconConflictException(
                "Sessions are not checked against Beacon's user store on this host (user management is off), so they cannot be signed out everywhere.");
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var userId = request.UserId ?? OwnUserId(actor);
        if (request.UserId != null)
        {
            await rules.DemandMayChangeUserAsync(userId, cancellationToken);
        }

        if (!await sessions.RenewStampAsync(userId, cancellationToken))
        {
            throw request.UserId == null
                ? new BeaconConflictException("Your user could not be found, so your sessions were not signed out everywhere.")
                : new BeaconNotFoundException($"User {userId} not found.");
        }

        logger.LogInformation(
            ProjectAccessEvents.UserSessionsRevoked,
            "Every session of user {UserId} signed out, by user {ActorId}",
            userId,
            actor.LogId);
    }

    // The caller's own stored user, for a signed-in session only: an API key or an MCP caller holds no session to end.
    private static int OwnUserId(BeaconActor actor)
    {
        if (actor is { Kind: BeaconPrincipalKind.User, BeaconUserId: { } userId })
        {
            return userId;
        }

        throw new BeaconConflictException("Only a signed-in session of a Beacon user can sign out everywhere.");
    }
}

/// <summary>
/// Signs out every session user <c>UserId</c> holds: their security stamp changes, so each of their sessions is refused
/// when it is next revalidated (within <c>Beacon:Authentication:Session:RevalidateMinutes</c>) and they sign in again.
/// Their API keys are not affected. With a <c>UserId</c>, for an Admin, or a UserAdmin on another user who is no super
/// admin and holds neither the Admin role nor a functional role (403 otherwise); a user that does not exist answers 404.
/// Without one (sent by <c>POST /beacon/api/auth/logout?everywhere=true</c>), the signed-in caller's own sessions. Nothing
/// is changed, and the answer is 409, when the sessions could not be signed out: Beacon user management is off (sessions
/// are not checked against the user store), or the caller is no signed-in session of a stored user. Logged with ids only,
/// in one place for both.
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission (or the caller's own sessions); no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record SignUserOutEverywhereCommand(int? UserId) : IRequest;
