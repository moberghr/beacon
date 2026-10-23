using System.Security.Claims;
using Beacon.Core.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.Api.SignalR;

/// <summary>
/// The id <see cref="Hubs.BeaconHub"/> addresses a connection by, and so the id every publisher passes to
/// <c>Clients.User(...)</c>: the signed-in session's <c>Users.ExternalId</c> (its <c>NameIdentifier</c>), which is the
/// <see cref="BeaconActor.UserId"/> Beacon records for a person (a change's requester and reviewer, a job's
/// enqueuer). A principal that names its user by another id, an API key or a mapped MCP caller (both by the numeric
/// <c>Users.Id</c>), gets no id and receives no user-addressed event: the two id spaces never meet. Registered by
/// <c>AddBeaconApiServices</c> in place of SignalR's default; a provider the host registered itself is kept.
/// </summary>
public sealed class HubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return UserIdOf(connection.User);
    }

    internal static string? UserIdOf(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (BeaconUserLookup.IsApiKey(principal) || BeaconUserLookup.IsMcpCaller(principal))
        {
            return null;
        }

        return BeaconUserLookup.Of(principal).ExternalId;
    }
}
