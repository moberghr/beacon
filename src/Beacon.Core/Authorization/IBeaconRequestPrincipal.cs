using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Core.Authorization;

/// <summary>
/// The principal of the HTTP request a scope serves, as Core reads it without knowing the web stack. Off-request (a
/// background job, a worker host) <see cref="HasRequest"/> is false and <see cref="User"/> is null: the scope's caller is
/// then decided by run-as (<see cref="IRunAsScope"/>) or the system work mark (<see cref="ISystemWorkScope"/>). Core
/// registers one that never has a request; Beacon.Api and Beacon.MCP replace it with one that reads the current
/// <c>HttpContext</c>, and a host registers its own with <see cref="BeaconRequestPrincipalRegistration.UseRequestPrincipal{T}"/>.
/// </summary>
public interface IBeaconRequestPrincipal
{
    /// <summary>Whether the scope serves an HTTP request.</summary>
    bool HasRequest { get; }

    /// <summary>The request's principal; null off-request.</summary>
    ClaimsPrincipal? User { get; }
}

/// <summary>
/// A request principal Beacon registers itself: Core's off-request default (<see cref="NoRequestPrincipal"/>) and the
/// web host's <c>HttpContext</c> principal that Beacon.Api and Beacon.MCP register. An explicit
/// <see cref="BeaconRequestPrincipalRegistration.UseRequestPrincipal{T}"/> replaces every one of them.
/// </summary>
internal interface IBeaconOwnRequestPrincipal : IBeaconRequestPrincipal;

/// <summary>Core's default: no scope serves a request, as in a host without Beacon.Api or Beacon.MCP.</summary>
internal sealed class NoRequestPrincipal : IBeaconOwnRequestPrincipal
{
    public bool HasRequest => false;

    public ClaimsPrincipal? User => null;
}

public static class BeaconRequestPrincipalRegistration
{
    /// <summary>
    /// Registers <typeparamref name="T"/> as the request principal, in any order with Beacon's own registrations: every
    /// principal Beacon registered itself (Core's off-request default, the <c>HttpContext</c> principal of Beacon.Api and
    /// Beacon.MCP) is removed and <typeparamref name="T"/> is added. Core's default registration after it is a no-op, and
    /// Beacon.Api's and Beacon.MCP's registrations after it keep it. A principal the host registered some other way (its
    /// own type with <c>AddSingleton</c>) is not removed; the last registration wins, also over an earlier
    /// <see cref="UseRequestPrincipal{T}"/> call.
    /// </summary>
    public static IServiceCollection UseRequestPrincipal<T>(this IServiceCollection services)
        where T : class, IBeaconRequestPrincipal
    {
        // Beacon's own principals and an earlier registration of T go; T is added last, so the latest call wins.
        var replaced = services
            .Where(x => x.ServiceType == typeof(IBeaconRequestPrincipal))
            .Where(x => x.ImplementationType != null)
            .Where(x => x.ImplementationType == typeof(T) || typeof(IBeaconOwnRequestPrincipal).IsAssignableFrom(x.ImplementationType))
            .ToList();
        foreach (var descriptor in replaced)
        {
            services.Remove(descriptor);
        }

        services.AddSingleton<IBeaconRequestPrincipal, T>();

        return services;
    }

    /// <summary>
    /// Beacon.Api's and Beacon.MCP's registration of their web principal: replaces only Core's off-request default
    /// (<see cref="NoRequestPrincipal"/>) and adds <typeparamref name="T"/> when no principal is left, so any principal
    /// already registered (the host's own, or the other web registration's) stays, and Core's default registration after
    /// it is a no-op.
    /// </summary>
    internal static IServiceCollection TryUseRequestPrincipal<T>(this IServiceCollection services)
        where T : class, IBeaconOwnRequestPrincipal
    {
        var defaults = services
            .Where(x => x.ServiceType == typeof(IBeaconRequestPrincipal))
            .Where(x => x.ImplementationType == typeof(NoRequestPrincipal))
            .ToList();
        foreach (var descriptor in defaults)
        {
            services.Remove(descriptor);
        }

        services.TryAddSingleton<IBeaconRequestPrincipal, T>();

        return services;
    }
}
