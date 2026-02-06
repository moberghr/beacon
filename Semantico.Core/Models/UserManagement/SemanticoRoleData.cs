namespace Semantico.Core.Models.UserManagement;

/// <summary>
/// Data transfer object for Semantico roles.
/// </summary>
public class SemanticoRoleData
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsSystemRole { get; set; }

    public int Level { get; set; }
}
