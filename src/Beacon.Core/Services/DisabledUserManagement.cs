#pragma warning disable CS0618 // implements the members the interface marks obsolete
using Beacon.Core.Authentication;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;

namespace Beacon.Core.Services;

/// <summary>
/// Registered while user management is off, so every handler that needs the user store still resolves and the container
/// validates on build. Every member refuses with <see cref="BeaconForbiddenException"/>. Code that treats an absent user
/// store as "user management is off" reads the service through <see cref="DisabledUserManagement.OrNull"/>.
/// </summary>
public sealed class DisabledUserManagementService : IUserManagementService
{
    public Task<bool> IsFirstRunAsync(CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BeaconUserData> CreateSuperAdminAsync(CreateSuperAdminRequest request, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<List<BeaconUserData>> GetUsersAsync(string? search = null, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BeaconUserData?> GetUserByIdAsync(int userId, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BeaconUserData?> GetUserByExternalIdAsync(string externalId, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BeaconUserData?> GetUserByExternalIdAndProviderAsync(string externalId, string? identityProvider, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<List<BearerUserCandidate>> GetBearerUserCandidatesAsync(
        string externalId,
        string identityProvider,
        bool includeWithoutIdentityProvider,
        CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BeaconUserData?> GetUserByUserNameAsync(string userName, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> CreateInternalUserAsync(CreateInternalUserRequest request, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> CreateExternalUserAsync(CreateExternalUserRequest request, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BeaconUserData> GetOrCreateExternalUserAsync(
        string externalId,
        string identityProvider,
        string userName,
        string? email,
        string? displayName,
        string? defaultRoleName,
        CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> DeleteUserAsync(int userId, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> ToggleUserEnabledAsync(int userId, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> AssignRoleAsync(int userId, int roleId, string? assignedBy, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> RemoveRoleAsync(int userId, int roleId, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<BaseResponse> ResetPasswordAsync(int userId, string newPassword, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<AuthenticationResult> AuthenticateInternalUserAsync(string username, string password, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task<AuthenticationResult> CompleteRequiredPasswordChangeAsync(
        string username,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task UpdateLastLoginAsync(int userId, CancellationToken ct = default) => throw DisabledUserManagement.Refusal();
}

/// <summary>The role service registered while user management is off; see <see cref="DisabledUserManagementService"/>.</summary>
public sealed class DisabledRoleService : IRoleService
{
    public Task<List<BeaconRoleData>> GetRolesAsync(CancellationToken ct = default) => throw DisabledUserManagement.Refusal();

    public Task SeedSystemRolesAsync(CancellationToken ct = default) => throw DisabledUserManagement.Refusal();
}

public static class DisabledUserManagement
{
    public const string Message = "User management is off.";

    /// <summary>
    /// The user store, or null while user management is off (the stand-in registered then counts as absent), for code
    /// that falls back when there is no user store.
    /// </summary>
    public static IUserManagementService? OrNull(this IUserManagementService? users)
    {
        return users is DisabledUserManagementService ? null : users;
    }

    internal static BeaconForbiddenException Refusal()
    {
        return new BeaconForbiddenException(Message);
    }
}
