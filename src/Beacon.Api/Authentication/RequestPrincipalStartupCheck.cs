using Beacon.Core.Authorization;

namespace Beacon.Api.Authentication;

/// <summary>
/// Fails startup when Beacon's endpoints are served but Core still reads the request's caller through its off-request
/// default (<see cref="NoRequestPrincipal"/>): every request would then act as nobody or, in a scope marked as system
/// work, as the system. The Beacon.Api registrations replace that default with the web host's principal; a host that
/// registered its own (<see cref="BeaconRequestPrincipalRegistration.UseRequestPrincipal{T}"/>) keeps it and passes.
/// The principal is required: Beacon's endpoints are served over Beacon.Core, which always registers one.
/// </summary>
internal sealed class RequestPrincipalStartupCheck(IBeaconRequestPrincipal requestPrincipal) : IStartupFilter
{
    internal const string Message =
        "Beacon's endpoints are mapped, but Beacon reads no request's caller: IBeaconRequestPrincipal is Beacon.Core's off-request " +
        "default. Call AddBeaconApiServices or AddBeaconCookieAuthentication, or register a principal with UseRequestPrincipal.";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (requestPrincipal is NoRequestPrincipal)
        {
            throw new InvalidOperationException(Message);
        }

        return next;
    }
}
