namespace Beacon.Core;

/// <summary>
/// Configuration options for user management.
/// Enables internal user management with roles (Admin, Editor, Viewer).
/// </summary>
public class UserManagementOptions
{
    /// <summary>
    /// Enable user management feature. Default: false (backward compatible, opt-in)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Allow creation of internal users with passwords stored in Beacon.
    /// Default: true
    /// </summary>
    public bool AllowInternalUsers { get; set; } = true;

    /// <summary>
    /// Minimum password length for internal users.
    /// Default: 8
    /// </summary>
    public int MinimumPasswordLength { get; set; } = 8;

    /// <summary>
    /// Require password complexity (uppercase, lowercase, digit, special char).
    /// Default: true
    /// </summary>
    public bool RequirePasswordComplexity { get; set; } = true;
}
