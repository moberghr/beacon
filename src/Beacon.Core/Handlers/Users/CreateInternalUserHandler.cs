using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Users;

internal sealed class CreateInternalUserHandler(IUserManagementService userService)
    : IRequestHandler<CreateInternalUserCommand>
{
    public async Task Handle(CreateInternalUserCommand request, CancellationToken cancellationToken)
    {
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
    }
}

[ProjectScopeExempt("admin-gated user management, no project data", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record CreateInternalUserCommand(
    string UserName,
    string? Email,
    string? DisplayName,
    string Password,
    int[] RoleIds) : IRequest;
