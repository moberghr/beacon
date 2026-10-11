using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Users;

internal sealed class CreateExternalUserHandler(
    IUserManagementService userService,
    UserAdministrationRules rules)
    : IRequestHandler<CreateExternalUserCommand>
{
    public async Task Handle(CreateExternalUserCommand request, CancellationToken cancellationToken)
    {
        // The roles are checked before the user is created: a refused role creates nobody.
        var roles = await rules.RolesAsync(request.RoleIds, cancellationToken);
        await rules.DemandMayAssignAsync(null, roles, roles, cancellationToken);

        var serviceRequest = new CreateExternalUserRequest
        {
            ExternalId = request.ExternalId,
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName,
            RoleIds = request.RoleIds.ToList(),
        };

        var response = await userService.CreateExternalUserAsync(serviceRequest, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "Failed to create user.");
        }

        await rules.LogAssignedRolesAsync(request.UserName, roles, cancellationToken);
    }
}

/// <summary>
/// Creates an external user with <c>RoleIds</c>. For an Admin or a UserAdmin; only an Admin assigns the Admin role or a functional
/// role (403), and separation of duties, when on, refuses a conflicting pair (400). The roles assigned are logged.
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record CreateExternalUserCommand(
    string ExternalId,
    string UserName,
    string? Email,
    string? DisplayName,
    int[] RoleIds) : IRequest;
