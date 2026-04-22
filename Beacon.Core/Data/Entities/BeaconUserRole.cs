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
    /// External ID of the user who assigned this role.
    /// </summary>
    public string? AssignedByUserId { get; set; }

    /// <summary>
    /// When the role was assigned.
    /// </summary>
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
