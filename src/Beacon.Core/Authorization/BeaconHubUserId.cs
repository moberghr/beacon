using System.Security.Claims;

namespace Beacon.Core.Authorization;

/// <summary>
/// The id the realtime hub addresses a connection by, and so the id a job's <c>JobStatusChanged</c> events are published
/// to (<c>Clients.User(...)</c>): the signed-in session's <c>Users.ExternalId</c> (its <c>NameIdentifier</c>), what
/// <see cref="BeaconActor.UserId"/> records for a person. A principal that names its user by another id, an API key or a
/// mapped MCP caller (both by the numeric <c>Users.Id</c>, or a subject hash), gets none and receives no user-addressed
/// event: the two id spaces never meet, so a numeric id can never reach a user whose external id happens to read the same.
/// Beacon.Api's hub user id provider and every enqueue that names who to notify read it here, so the two never differ.
/// </summary>
internal static class BeaconHubUserId
{
    public static string? Of(ClaimsPrincipal? principal)
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
