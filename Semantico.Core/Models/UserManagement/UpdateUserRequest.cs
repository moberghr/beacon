using System.ComponentModel.DataAnnotations;

namespace Semantico.Core.Models.UserManagement;

/// <summary>
/// Request to update an existing user.
/// </summary>
public class UpdateUserRequest
{
    [Required]
    public int UserId { get; set; }

    [Required]
    [MinLength(3)]
    public string UserName { get; set; } = null!;

    [EmailAddress]
    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public bool IsEnabled { get; set; }
}
