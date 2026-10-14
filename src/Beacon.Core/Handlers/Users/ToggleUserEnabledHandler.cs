using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Users;

internal sealed class ToggleUserEnabledHandler(IUserManagementService userService, UserAdministrationRules rules)
    : IRequestHandler<ToggleUserEnabledCommand>
{
    public async Task Handle(ToggleUserEnabledCommand request, CancellationToken cancellationToken)
    {
        await rules.DemandMayChangeUserAsync(request.UserId, cancellationToken);

        var response = await userService.ToggleUserEnabledAsync(request.UserId, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "Failed to toggle user state.");
        }
    }
}

/// <summary>
/// Enables or disables a user. For an Admin, or a UserAdmin on another user who is no super admin and holds
/// neither the Admin role nor a functional role (403 otherwise).
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record ToggleUserEnabledCommand(int UserId) : IRequest;
