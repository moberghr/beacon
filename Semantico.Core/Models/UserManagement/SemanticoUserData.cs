namespace Semantico.Core.Models.UserManagement;

/// <summary>
/// Data transfer object for Semantico users.
/// </summary>
public class SemanticoUserData
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

    public List<SemanticoRoleData> Roles { get; set; } = new();
}
