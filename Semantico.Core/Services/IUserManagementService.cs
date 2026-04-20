using Semantico.Core.Authentication;
using Semantico.Core.Helpers;
using Semantico.Core.Models.UserManagement;

namespace Semantico.Core.Services;

/// <summary>
/// Service for managing Semantico users.
/// </summary>
public interface IUserManagementService
{
    // Setup
    /// <summary>
    /// Checks if this is the first run (no users exist).
    /// </summary>
    Task<bool> IsFirstRunAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates the initial super admin account.
    /// </summary>
    Task<SemanticoUserData> CreateSuperAdminAsync(CreateSuperAdminRequest request, CancellationToken ct = default);

    // User CRUD
    /// <summary>
    /// Gets all users, optionally filtered by search term.
    /// </summary>
    Task<List<SemanticoUserData>> GetUsersAsync(string? search = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their internal ID.
    /// </summary>
    Task<SemanticoUserData?> GetUserByIdAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their external ID.
    /// </summary>
    Task<SemanticoUserData?> GetUserByExternalIdAsync(string externalId, CancellationToken ct = default);

    Task<SemanticoUserData?> GetUserByExternalIdAndProviderAsync(string externalId, string? identityProvider, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their username.
    /// </summary>
    Task<SemanticoUserData?> GetUserByUserNameAsync(string userName, CancellationToken ct = default);

    /// <summary>
    /// Creates a new internal user (password stored in Semantico).
    /// </summary>
    Task<BaseResponse> CreateInternalUserAsync(CreateInternalUserRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a new external user (authenticated via JWT/OAuth).
    /// </summary>
    Task<BaseResponse> CreateExternalUserAsync(CreateExternalUserRequest request, CancellationToken ct = default);

    Task<SemanticoUserData> GetOrCreateExternalUserAsync(
        string externalId,
        string identityProvider,
        string userName,
        string? email,
        string? displayName,
        string defaultRoleName,
        CancellationToken ct = default);

    /// <summary>
    /// Updates an existing user.
    /// </summary>
    Task<BaseResponse> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes (archives) a user.
    /// </summary>
    Task<BaseResponse> DeleteUserAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Toggles a user's enabled status.
    /// </summary>
    Task<BaseResponse> ToggleUserEnabledAsync(int userId, CancellationToken ct = default);

    // Role assignment
    /// <summary>
    /// Assigns a role to a user.
    /// </summary>
    Task<BaseResponse> AssignRoleAsync(int userId, int roleId, string? assignedBy, CancellationToken ct = default);

    /// <summary>
    /// Removes a role from a user.
    /// </summary>
    Task<BaseResponse> RemoveRoleAsync(int userId, int roleId, CancellationToken ct = default);

    // Password management
    /// <summary>
    /// Changes an internal user's password.
    /// </summary>
    Task<BaseResponse> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>
    /// Resets an internal user's password (admin action).
    /// </summary>
    Task<BaseResponse> ResetPasswordAsync(int userId, string newPassword, CancellationToken ct = default);

    // Authentication
    /// <summary>
    /// Authenticates an internal user with username/password.
    /// </summary>
    Task<AuthenticationResult> AuthenticateInternalUserAsync(string username, string password, CancellationToken ct = default);

    /// <summary>
    /// Updates the last login timestamp for a user.
    /// </summary>
    Task UpdateLastLoginAsync(string externalId, CancellationToken ct = default);
}
