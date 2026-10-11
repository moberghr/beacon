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
        // The caller, then the roles, are checked before the user is created: a refusal creates nobody.
        await rules.DemandMayCreateInternalUserAsync(cancellationToken);
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

        await rules.LogCreatedAsync(request.UserName, true, roles, cancellationToken);
    }
}

/// <summary>
/// Creates an internal (password) user with <c>RoleIds</c>. For an Admin only (403 for a UserAdmin who is not one): whoever
/// sets the password can sign in as the user. Separation of duties, when on, refuses a conflicting pair (400). The new user
/// and the roles assigned are logged.
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission and, by the handler, Admin-only; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record CreateInternalUserCommand(
    string UserName,
    string? Email,
    string? DisplayName,
    string Password,
    int[] RoleIds) : IRequest;
