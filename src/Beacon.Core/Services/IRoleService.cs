using Beacon.Core.Models.UserManagement;

namespace Beacon.Core.Services;

/// <summary>
/// Service for managing predefined roles.
/// </summary>
public interface IRoleService
{
    /// <summary>
    /// Gets all available roles.
    /// </summary>
    Task<List<BeaconRoleData>> GetRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Seeds the predefined system roles (Admin, Editor, Viewer) if they don't exist.
    /// </summary>
    Task SeedSystemRolesAsync(CancellationToken ct = default);
}
