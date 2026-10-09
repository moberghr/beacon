using Beacon.Core.Authentication;
using Beacon.Core.Helpers;
using Beacon.Core.Models.UserManagement;

namespace Beacon.Core.Services;

/// <summary>
/// Service for managing Beacon users.
/// </summary>
public interface IUserManagementService
{
    // Setup
    /// <summary>
    /// Checks if this is the first run: no super admin has ever existed (an archived super admin counts, so archiving
    /// it never reopens first-run setup).
    /// </summary>
    Task<bool> IsFirstRunAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates the initial super admin account.
    /// </summary>
    Task<BeaconUserData> CreateSuperAdminAsync(CreateSuperAdminRequest request, CancellationToken ct = default);

    // User CRUD
    /// <summary>
    /// Gets all users, optionally filtered by search term.
    /// </summary>
    Task<List<BeaconUserData>> GetUsersAsync(string? search = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their internal ID.
    /// </summary>
    Task<BeaconUserData?> GetUserByIdAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their external ID.
    /// </summary>
    Task<BeaconUserData?> GetUserByExternalIdAsync(string externalId, CancellationToken ct = default);

    Task<BeaconUserData?> GetUserByExternalIdAndProviderAsync(string externalId, string? identityProvider, CancellationToken ct = default);

    /// <summary>
    /// The external users a bearer token with subject <paramref name="externalId"/> and issuer
    /// <paramref name="identityProvider"/> may be bound to, in one query: users keyed by that pair and, when
    /// <paramref name="includeWithoutIdentityProvider"/> is set, users pre-registered with that external id and no
    /// identity provider. Internal (password) users and super admins are never returned. Archived users are returned
    /// and flagged, so the caller refuses them.
    /// </summary>
    Task<List<BearerUserCandidate>> GetBearerUserCandidatesAsync(
        string externalId,
        string identityProvider,
        bool includeWithoutIdentityProvider,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their username.
    /// </summary>
    Task<BeaconUserData?> GetUserByUserNameAsync(string userName, CancellationToken ct = default);

    /// <summary>
    /// Creates a new internal user (password stored in Beacon).
    /// </summary>
    Task<BaseResponse> CreateInternalUserAsync(CreateInternalUserRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a new external user (authenticated via JWT/OAuth).
    /// </summary>
    Task<BaseResponse> CreateExternalUserAsync(CreateExternalUserRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the external user keyed by (<paramref name="externalId"/>, <paramref name="identityProvider"/>), creating
    /// it on first sign-in. A null or blank <paramref name="defaultRoleName"/> creates the user with no role. Throws
    /// <see cref="Models.BeaconException"/> for a disabled or archived user, and refuses to create a user before first-run
    /// setup has created the super admin.
    /// </summary>
    Task<BeaconUserData> GetOrCreateExternalUserAsync(
        string externalId,
        string identityProvider,
        string userName,
        string? email,
        string? displayName,
        string? defaultRoleName,
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
    /// Updates the last login timestamp of the user with Beacon id <paramref name="userId"/>.
    /// </summary>
    Task UpdateLastLoginAsync(int userId, CancellationToken ct = default);
}
