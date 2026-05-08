using Beacon.Core.Authorization;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.UserSettings;

internal sealed class GetUserSettingsHandler(
    IBeaconUserContext userContext,
    IUserManagementService userManagement)
    : IRequestHandler<GetUserSettingsQuery, GetUserSettingsResult>
{
    public async Task<GetUserSettingsResult> Handle(GetUserSettingsQuery request, CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrEmpty(userContext.UserName))
        {
            throw new InvalidOperationException("No authenticated user.");
        }

        var user = await userManagement.GetUserByUserNameAsync(userContext.UserName, cancellationToken)
            ?? throw new InvalidOperationException("Current user not found.");

        var roles = user.Roles
            .Select(x => x.Name)
            .ToList();

        return new GetUserSettingsResult(new UserSettingsView(
            user.UserName,
            user.Email,
            user.DisplayName,
            user.IsInternalUser,
            roles));
    }
}

public record GetUserSettingsQuery : IRequest<GetUserSettingsResult>;

public record GetUserSettingsResult(UserSettingsView User);

public record UserSettingsView(
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsInternalUser,
    List<string> Roles);
