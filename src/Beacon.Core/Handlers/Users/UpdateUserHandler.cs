using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Users;

internal sealed class UpdateUserHandler(IUserManagementService userService, UserAdministrationRules rules)
    : IRequestHandler<UpdateUserCommand>
{
    public async Task Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        await rules.DemandMayChangeUserAsync(request.UserId, cancellationToken);
        await rules.DemandMayChangeSignInIdentityAsync(request.UserId, request.UserName, request.Email, cancellationToken);

        var serviceRequest = new UpdateUserRequest
        {
            UserId = request.UserId,
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName,
            IsEnabled = request.IsEnabled,
        };

        var response = await userService.UpdateUserAsync(serviceRequest, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "Failed to update user.");
        }
    }
}

/// <summary>
/// Updates a user's account. For an Admin, or a UserAdmin on another user who is no super admin and holds
/// neither the Admin role nor a functional role (403 otherwise). The user name and the e-mail address, which a user signs
/// in with, are an Admin's to change: a UserAdmin who sends others than the stored ones is refused (403).
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record UpdateUserCommand(
    int UserId,
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled) : IRequest;
