using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Join table for the many-to-many relationship between users and roles.
/// Tracks who assigned the role and when.
/// </summary>
public class BeaconUserRole : BaseEntity
{
    /// <summary>
    /// Foreign key to the user.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public BeaconUser User { get; set; } = null!;

    /// <summary>
    /// Foreign key to the role.
    /// </summary>
    public int RoleId { get; set; }

    /// <summary>
    /// Navigation property to the role.
    /// </summary>
    public BeaconRole Role { get; set; } = null!;

    /// <summary>
    /// External ID of the user who assigned this role: recorded when an administrator creates the user or changes their
    /// roles. Null when Beacon granted it itself (the default role at a first single sign-on or MCP sign-in, the super
    /// admin's Admin role at first-run setup) and for assignments made before it was recorded.
    /// </summary>
    public string? AssignedByUserId { get; set; }

    /// <summary>
    /// When the role was assigned.
    /// </summary>
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
