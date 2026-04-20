using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Represents a user in the Semantico system.
/// Supports both internal users (password stored in Semantico) and external users (authenticated via JWT/OAuth).
/// </summary>
public class SemanticoUser : ArchivableBaseEntity
{
    /// <summary>
    /// Unique external identifier for the user.
    /// For internal users, this is a generated GUID.
    /// For external users, this matches the 'sub' claim from JWT or OAuth provider.
    /// </summary>
    public string ExternalId { get; set; } = null!;

    public string? IdentityProvider { get; set; }

    /// <summary>
    /// Username used for authentication (internal users) or display (external users).
    /// </summary>
    public string UserName { get; set; } = null!;

    /// <summary>
    /// User's email address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Display name for the user.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// True if this user authenticates with a password stored in Semantico.
    /// False if this user is authenticated externally (JWT/OAuth).
    /// </summary>
    public bool IsInternalUser { get; set; }

    /// <summary>
    /// Hashed password for internal users. Null for external users.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Salt used for password hashing. Null for external users.
    /// </summary>
    public string? PasswordSalt { get; set; }

    /// <summary>
    /// True if this user has super admin privileges.
    /// Super admins can manage all users and have full system access.
    /// </summary>
    public bool IsSuperAdmin { get; set; }

    /// <summary>
    /// Whether the user account is enabled.
    /// Disabled users cannot log in.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Timestamp of the user's last successful login.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Roles assigned to this user.
    /// </summary>
    public List<SemanticoUserRole> UserRoles { get; set; } = new();
}
