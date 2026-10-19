using System.Security.Claims;
using Beacon.Api;
using Beacon.Core.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Tests.Common;

/// <summary>
/// The request principal Core classes read in tests: no request (<see cref="OffRequest"/>), a request with a given caller
/// (<see cref="For"/>), the live <c>HttpContext</c> of an accessor a test shares with Api classes (<see cref="Of"/>), or the
/// web host's own principal as Beacon.Api or Beacon.MCP registers it (<see cref="Web"/>).
/// </summary>
internal sealed class TestRequestPrincipal(bool hasRequest, ClaimsPrincipal? user) : IBeaconRequestPrincipal
{
    public bool HasRequest { get; } = hasRequest;

    public ClaimsPrincipal? User { get; } = user;

    /// <summary>A scope without a request: a background job or a worker host.</summary>
    public static TestRequestPrincipal OffRequest()
    {
        return new TestRequestPrincipal(false, null);
    }

    /// <summary>A request whose caller is <paramref name="user"/>; without one, an anonymous caller (as a fresh request has).</summary>
    public static TestRequestPrincipal For(ClaimsPrincipal? user = null)
    {
        return new TestRequestPrincipal(true, user ?? new ClaimsPrincipal(new ClaimsIdentity()));
    }

    /// <summary>The request of <paramref name="accessor"/> as it is when read, as the web host's principal reads it.</summary>
    public static IBeaconRequestPrincipal Of(IHttpContextAccessor accessor)
    {
        return new AccessorPrincipal(accessor);
    }

    /// <summary>
    /// The principal Beacon's web registration (Beacon.Api's, or Beacon.MCP's with <paramref name="throughMcp"/>) gives
    /// a host whose request <paramref name="accessor"/> holds. Resolved from a container, never named: both assemblies
    /// compile their own copy of the type.
    /// </summary>
    public static IBeaconRequestPrincipal Web(IHttpContextAccessor accessor, bool throughMcp = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        if (throughMcp)
        {
            Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);
        }
        else
        {
            services.AddBeaconApiServices();
        }

        return services
            .BuildServiceProvider()
            .GetRequiredService<IBeaconRequestPrincipal>();
    }

    private sealed class AccessorPrincipal(IHttpContextAccessor accessor) : IBeaconRequestPrincipal
    {
        public bool HasRequest => accessor.HttpContext != null;

        public ClaimsPrincipal? User => accessor.HttpContext?.User;
    }
}

internal static class TestRequestPrincipalRegistration
{
    /// <summary>
    /// The web host's own request principal (<see cref="TestRequestPrincipal.Web"/>) over the host's accessor, for a test
    /// host that maps Beacon's endpoints without <c>AddBeaconApiServices</c>; a principal already registered stays.
    /// </summary>
    public static IServiceCollection AddWebRequestPrincipal(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(x => TestRequestPrincipal.Web(x.GetRequiredService<IHttpContextAccessor>()));

        return services;
    }
}
