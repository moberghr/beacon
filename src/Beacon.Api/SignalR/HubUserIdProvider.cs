using Beacon.Core.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.Api.SignalR;

/// <summary>
/// The id <see cref="Hubs.BeaconHub"/> addresses a connection by, and so the id every publisher passes to
/// <c>Clients.User(...)</c>: <see cref="BeaconHubUserId.Of"/>, the signed-in session's <c>Users.ExternalId</c>. An API key
/// or a mapped MCP caller gets no id and receives no user-addressed event.
/// </summary>
public sealed class HubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return BeaconHubUserId.Of(connection.User);
    }
}
