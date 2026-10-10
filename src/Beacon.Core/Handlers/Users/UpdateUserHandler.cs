using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Users;

internal sealed class UpdateUserHandler(IUserManagementService userService)
    : IRequestHandler<UpdateUserCommand>
{
    public async Task Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
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

[ProjectScopeExempt("admin-gated user management, no project data", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record UpdateUserCommand(
    int UserId,
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled) : IRequest;
