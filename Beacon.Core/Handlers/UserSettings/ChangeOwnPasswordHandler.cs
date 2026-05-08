using Beacon.Core.Authorization;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.UserSettings;

internal sealed class ChangeOwnPasswordHandler(
    IBeaconUserContext userContext,
    IUserManagementService userManagement)
    : IRequestHandler<ChangeOwnPasswordCommand>
{
    public async Task Handle(ChangeOwnPasswordCommand request, CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrEmpty(userContext.UserName))
        {
            throw new InvalidOperationException("No authenticated user.");
        }

        var user = await userManagement.GetUserByUserNameAsync(userContext.UserName, cancellationToken)
            ?? throw new InvalidOperationException("Current user not found.");

        if (!user.IsInternalUser)
        {
            throw new InvalidOperationException("Only internal users can change passwords here.");
        }

        var response = await userManagement.ChangePasswordAsync(
            user.Id,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }
    }
}

public record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword) : IRequest;
