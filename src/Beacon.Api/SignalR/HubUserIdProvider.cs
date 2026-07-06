using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.Api.SignalR;

/// <summary>
/// Resolves the SignalR user-id token from the authenticated user's NameIdentifier claim.
/// Used by <c>Clients.User(...)</c> in <see cref="Hubs.BeaconHub"/> publishers.
/// </summary>
public sealed class HubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
