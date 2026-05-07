using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.SampleProject.SignalR;

/// <summary>
/// Resolves the SignalR user-id token from the authenticated user's NameIdentifier claim.
/// Used by <c>Clients.User(...)</c> in <see cref="Hubs.BeaconHub"/> publishers.
/// </summary>
internal sealed class HubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
