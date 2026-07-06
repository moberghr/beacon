namespace Beacon.Core.Models.UserManagement;

/// <summary>
/// Data transfer object for Beacon roles.
/// </summary>
public class BeaconRoleData
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsSystemRole { get; set; }

    public int Level { get; set; }
}
