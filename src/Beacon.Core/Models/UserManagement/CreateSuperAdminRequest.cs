using System.ComponentModel.DataAnnotations;

namespace Beacon.Core.Models.UserManagement;

/// <summary>
/// Request to create the initial super admin account during first-run setup.
/// </summary>
public class CreateSuperAdminRequest
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

    [Required]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = null!;

    /// <summary>
    /// The first-run setup token: the configured <c>Beacon:UserManagement:SetupToken</c>, or the one Beacon wrote to
    /// the server log at startup. Compared in constant time; never logged or echoed.
    /// </summary>
    public string? SetupToken { get; set; }
}
