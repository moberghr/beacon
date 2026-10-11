using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Users;

internal sealed class CreateInternalUserHandler(
    IUserManagementService userService,
    UserAdministrationRules rules)
    : IRequestHandler<CreateInternalUserCommand>
{
    public async Task Handle(CreateInternalUserCommand request, CancellationToken cancellationToken)
    {
        // The roles are checked before the user is created: a refused role creates nobody.
        var roles = await rules.RolesAsync(request.RoleIds, cancellationToken);
        await rules.DemandMayAssignAsync(null, roles, roles, cancellationToken);

        var serviceRequest = new CreateInternalUserRequest
        {
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName,
            Password = request.Password,
            RoleIds = request.RoleIds.ToList(),
        };

        var response = await userService.CreateInternalUserAsync(serviceRequest, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "Failed to create user.");
        }

        await rules.LogAssignedRolesAsync(request.UserName, roles, cancellationToken);
    }
}

/// <summary>
/// Creates an internal (password) user with <c>RoleIds</c>. For an Admin or a UserAdmin; only an Admin assigns the Admin role or a functional
/// role (403), and separation of duties, when on, refuses a conflicting pair (400). The roles assigned are logged.
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record CreateInternalUserCommand(
    string UserName,
    string? Email,
    string? DisplayName,
    string Password,
    int[] RoleIds) : IRequest;
