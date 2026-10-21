using System.Collections.Concurrent;
using System.Security.Claims;
using Beacon.Core.Mcp;

namespace Beacon.Testing;

/// <summary>
/// An in-memory <see cref="IMcpHostUserResolver"/>: the people a test says exist, by directory object id. Register the
/// instance with <see cref="BeaconTestingServiceCollectionExtensions.UseMcpTestUsers"/>; a test can remove a person to
/// prove that a deactivated account loses MCP on its next call.
/// </summary>
public sealed class McpTestUsers : IMcpHostUserResolver
{
    private readonly ConcurrentDictionary<string, McpHostUser> _people = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds the person <paramref name="objectId"/>: the host user <paramref name="id"/>, whose tool calls carry <paramref name="claims"/>.</summary>
    public McpTestUsers Add(string objectId, string id, string displayName, params Claim[] claims)
    {
        _people[objectId] = new McpHostUser(id, displayName, claims);

        return this;
    }

    /// <summary>Removes the person, as deactivating their account in the host would.</summary>
    public McpTestUsers Remove(string objectId)
    {
        _people.TryRemove(objectId, out _);

        return this;
    }

    public Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken cancellationToken) =>
        Task.FromResult(_people.TryGetValue(token.ObjectId, out var user) ? user : null);
}
