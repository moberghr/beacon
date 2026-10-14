using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Authentication;

/// <summary>
/// The last word on a password sign-in while local login is off (<see cref="LocalLoginOptions.Enabled"/> false), whichever
/// provider authenticated it (Beacon's own, the external JWT login, a host's): it stands only for a stored internal user,
/// enabled and not archived, whose own stored user name is on <see cref="LocalLoginOptions.BreakGlassUserNames"/> (an exact
/// match), and who is the user the session would name. With local login on every authenticated sign-in stands. Applied by
/// the login endpoint and <c>IBeaconAuthenticationService.SignInAsync</c>; a host without Beacon's data layer has no stored
/// user, so while local login is off nothing passes.
/// </summary>
internal sealed class PasswordSignInGate(BeaconConfiguration configuration, IDbContextFactory<BeaconContext> contextFactory)
{
    /// <summary>
    /// The id of the break-glass user <paramref name="result"/> signs in while local login is off, or null when the sign-in
    /// must be refused. Only called for a successful result while local login is off.
    /// </summary>
    public async Task<int?> BreakGlassUserIdAsync(AuthenticationResult result, CancellationToken cancellationToken)
    {
        var localLogin = configuration.UserManagement.LocalLogin;
        var externalId = result.User?.UserId;
        if (!result.Success || string.IsNullOrEmpty(externalId))
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.Users
            .Where(x => x.IsInternalUser)
            .Where(x => x.IsEnabled)
            .Where(x => x.ExternalId == externalId)
            .WhereIf(result.BeaconUserId != null, x => x.Id == result.BeaconUserId)
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.UserName
                })
            .FirstOrDefaultAsync(cancellationToken);

        return user != null && localLogin.IsBreakGlass(user.UserName) ? user.Id : null;
    }

    /// <summary>
    /// <see cref="BreakGlassUserIdAsync"/> through the request's services: null (refused) when the gate is not registered,
    /// so a host without Beacon's data layer signs nobody in with a password while local login is off.
    /// </summary>
    public static Task<int?> BreakGlassUserIdAsync(IServiceProvider services, AuthenticationResult result, CancellationToken cancellationToken)
    {
        var gate = services.GetService(typeof(PasswordSignInGate)) as PasswordSignInGate;

        return gate == null
            ? Task.FromResult<int?>(null)
            : gate.BreakGlassUserIdAsync(result, cancellationToken);
    }
}
