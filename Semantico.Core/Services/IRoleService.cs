using Semantico.Core.Models.UserManagement;

namespace Semantico.Core.Services;

/// <summary>
/// Service for managing predefined roles.
/// </summary>
public interface IRoleService
{
    /// <summary>
    /// Gets all available roles.
    /// </summary>
    Task<List<SemanticoRoleData>> GetRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a role by its name.
    /// </summary>
    Task<SemanticoRoleData?> GetRoleByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Gets a role by its ID.
    /// </summary>
    Task<SemanticoRoleData?> GetRoleByIdAsync(int roleId, CancellationToken ct = default);

    /// <summary>
    /// Seeds the predefined system roles (Admin, Editor, Viewer) if they don't exist.
    /// </summary>
    Task SeedSystemRolesAsync(CancellationToken ct = default);
}
