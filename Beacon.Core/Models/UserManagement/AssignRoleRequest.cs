using System.ComponentModel.DataAnnotations;

namespace Beacon.Core.Models.UserManagement;

/// <summary>
/// Request to assign a role to a user.
/// </summary>
public class AssignRoleRequest
{
    [Required]
    public int UserId { get; set; }

    [Required]
    public int RoleId { get; set; }
}
