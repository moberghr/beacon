using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Represents a role in the Beacon system.
/// Roles are predefined (Admin, Editor, Viewer) and cannot be created/deleted by users.
/// </summary>
public class BeaconRole : BaseEntity
{
    /// <summary>
    /// Role name (Admin, Editor, Viewer).
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Description of the role's permissions.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this is a system role that cannot be deleted.
    /// All predefined roles are system roles.
    /// </summary>
    public bool IsSystemRole { get; set; } = true;

    /// <summary>
    /// Permission level of the role.
    /// Admin=3, Editor=2, Viewer=1.
    /// Higher levels have more permissions.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Users assigned to this role.
    /// </summary>
    public List<BeaconUserRole> UserRoles { get; set; } = new();
}
