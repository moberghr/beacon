using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features.Authentication;

namespace Beacon.Api.Authentication;

/// <summary>
/// The request's authentication result as the framework's authentication middleware records it
/// (<see cref="IAuthenticateResultFeature"/> and <see cref="IHttpAuthenticationFeature"/>): the authorization middleware and
/// SignalR read the result from it (SignalR closes a hub connection when its <see cref="AuthenticationProperties.ExpiresUtc"/>
/// passes). Assigning another user drops the result, as the framework's own features do, so a stale result never outlives
/// the user it described.
/// </summary>
internal sealed class BeaconAuthenticationFeatures : IAuthenticateResultFeature, IHttpAuthenticationFeature
{
    private ClaimsPrincipal? _user;
    private AuthenticateResult? _result;

    private BeaconAuthenticationFeatures(AuthenticateResult result)
    {
        AuthenticateResult = result;
    }

    public AuthenticateResult? AuthenticateResult
    {
        get => _result;
        set
        {
            _result = value;
            _user = value?.Principal;
        }
    }

    public ClaimsPrincipal? User
    {
        get => _user;
        set
        {
            _user = value;
            _result = null;
        }
    }

    /// <summary>Records <paramref name="result"/> as the request's authentication result; its principal becomes the user.</summary>
    public static void Set(HttpContext context, AuthenticateResult result)
    {
        var features = new BeaconAuthenticationFeatures(result);
        context.Features.Set<IHttpAuthenticationFeature>(features);
        context.Features.Set<IAuthenticateResultFeature>(features);
    }
}
