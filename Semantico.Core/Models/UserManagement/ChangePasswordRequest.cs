using System.ComponentModel.DataAnnotations;

namespace Semantico.Core.Models.UserManagement;

/// <summary>
/// Request to change a user's password.
/// </summary>
public class ChangePasswordRequest
{
    [Required]
    public int UserId { get; set; }

    [Required]
    public string CurrentPassword { get; set; } = null!;

    [Required]
    [MinLength(8)]
    public string NewPassword { get; set; } = null!;

    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmNewPassword { get; set; } = null!;
}
