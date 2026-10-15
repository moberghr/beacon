using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.Core.Services.Security;

/// <summary>
/// What an API key grants: its scopes (the <see cref="McpCallerScope"/> names, Read and Execute) and its project list,
/// both stored as JSON on <c>ApiKeyCredential</c>. Validates the scopes of a new key and reads stored values back the
/// same way for authentication and for the key listings. The Execute scope never outlives its owner's write
/// permission: <see cref="OwnerCanWrite"/> and the authorization provider decide it when a key is issued and again on
/// every request.
/// </summary>
internal static class ApiKeyGrants
{
    /// <summary>
    /// The scopes requested for a new key, canonical and de-duplicated: at least one, each Read or Execute (any case).
    /// Throws <see cref="InvalidOperationException"/> otherwise, also for the retired Admin scope.
    /// </summary>
    public static string[] ParseRequestedScopes(IReadOnlyCollection<string>? requested)
    {
        if (requested == null || requested.Count == 0)
        {
            throw new InvalidOperationException("An API key needs at least one scope: Read or Execute.");
        }

        var scopes = new List<string>();
        foreach (var value in requested)
        {
            if (string.Equals(value?.Trim(), BeaconScopes.LegacyAdmin, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Admin scope can no longer be issued; use Execute.");
            }

            var scope = Enum.GetNames<McpCallerScope>()
                .Where(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Unknown API key scope. Valid scopes are Read and Execute.");

            if (!scopes.Contains(scope))
            {
                scopes.Add(scope);
            }
        }

        return [.. scopes];
    }

    /// <summary>
    /// The scopes a stored key grants: the retired Admin scope reads as Execute and unknown values are dropped.
    /// <c>null</c> when the stored value is malformed.
    /// </summary>
    public static string[]? ReadScopes(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        string[]? values;
        try
        {
            values = JsonSerializer.Deserialize<string[]>(stored);
        }
        catch (JsonException)
        {
            return null;
        }

        return (values ?? [])
            .Select(x => x == BeaconScopes.LegacyAdmin ? BeaconScopes.Execute : x)
            .Where(x => x == BeaconScopes.Read || x == BeaconScopes.Execute)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Whether a key owner has write permission, the condition for holding the Execute scope: a super admin, or a user
    /// with the Editor role or higher — the rule of the user-management authorization provider.
    /// </summary>
    public static bool OwnerCanWrite(bool isSuperAdmin, IEnumerable<int> roleLevels)
    {
        return isSuperAdmin || roleLevels.Any(x => x >= RoleService.RoleLevels.Editor);
    }

    /// <summary>
    /// The scopes a key acts with: its stored scopes, except that Execute reads as Read while its owner has no write
    /// permission.
    /// </summary>
    public static string[] ForOwner(IReadOnlyCollection<string> scopes, bool ownerCanWrite)
    {
        if (ownerCanWrite || !scopes.Contains(BeaconScopes.Execute))
        {
            return [.. scopes];
        }

        return scopes
            .Select(x => x == BeaconScopes.Execute ? BeaconScopes.Read : x)
            .Distinct()
            .ToArray();
    }

    /// <summary>The project ids a stored key is restricted to; <c>null</c> when none are stored, empty when malformed.</summary>
    public static int[]? ReadProjectIds(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<int[]>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
