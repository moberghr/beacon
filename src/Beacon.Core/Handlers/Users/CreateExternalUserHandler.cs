using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Users;

internal sealed class CreateExternalUserHandler(IUserManagementService userService)
    : IRequestHandler<CreateExternalUserCommand>
{
    public async Task Handle(CreateExternalUserCommand request, CancellationToken cancellationToken)
    {
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
    }
}

public record CreateExternalUserCommand(
    string ExternalId,
    string UserName,
    string? Email,
    string? DisplayName,
    int[] RoleIds) : IRequest;
