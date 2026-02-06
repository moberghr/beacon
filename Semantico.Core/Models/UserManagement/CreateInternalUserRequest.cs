using System.ComponentModel.DataAnnotations;

namespace Semantico.Core.Models.UserManagement;

/// <summary>
/// Request to create a new internal user (password stored in Semantico).
/// </summary>
public class CreateInternalUserRequest
{
    [Required]
    [MinLength(3)]
    public string UserName { get; set; } = null!;

    [EmailAddress]
    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    [Required]
    [MinLength(8)]
    public string Password { get; set; } = null!;

    /// <summary>
    /// Role IDs to assign to the user.
    /// </summary>
    public List<int> RoleIds { get; set; } = new();
}
