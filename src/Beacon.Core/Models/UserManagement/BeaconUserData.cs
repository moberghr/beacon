namespace Beacon.Core.Models.UserManagement;

/// <summary>
/// Data transfer object for Beacon users.
/// </summary>
public class BeaconUserData
{
    public int Id { get; set; }

    public string ExternalId { get; set; } = null!;

    public string? IdentityProvider { get; set; }

    public string UserName { get; set; } = null!;

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public bool IsInternalUser { get; set; }

    public bool IsSuperAdmin { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedTime { get; set; }

    /// <summary>
    /// The user's API-key generation (<c>BeaconUser.ApiKeyGeneration</c>), read in the same query as the rest of this
    /// record so a key issued on its strength is stamped with it. Read by <c>GetUserByExternalIdAsync</c>, the lookup
    /// API-key management resolves its caller with; the other lookups leave it 0.
    /// </summary>
    public int ApiKeyGeneration { get; set; }

    public List<BeaconRoleData> Roles { get; set; } = new();
}
