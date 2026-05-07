using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Users;

internal sealed class ToggleUserEnabledHandler(IUserManagementService userService)
    : IRequestHandler<ToggleUserEnabledCommand>
{
    public async Task Handle(ToggleUserEnabledCommand request, CancellationToken cancellationToken)
    {
        var response = await userService.ToggleUserEnabledAsync(request.UserId, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "Failed to toggle user state.");
        }
    }
}

public record ToggleUserEnabledCommand(int UserId) : IRequest;
