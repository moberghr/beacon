using System.ComponentModel.DataAnnotations;

namespace Semantico.Core.Models.UserManagement;

/// <summary>
/// Request to pre-register an external user (authenticated via JWT/OAuth).
/// </summary>
public class CreateExternalUserRequest
{
    /// <summary>
    /// External identifier that matches the 'sub' claim from JWT or OAuth provider.
    /// </summary>
    [Required]
    public string ExternalId { get; set; } = null!;

    [Required]
    [MinLength(3)]
    public string UserName { get; set; } = null!;

    [EmailAddress]
    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// Role IDs to assign to the user.
    /// </summary>
    public List<int> RoleIds { get; set; } = new();
}
