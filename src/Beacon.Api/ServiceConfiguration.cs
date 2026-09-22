using Beacon.Api.Hubs;
using Beacon.Api.SignalR;
using Beacon.Core.Authorization;
using Beacon.Core.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Api;

public static class ServiceConfiguration
{
    /// <summary>
    /// Registers Beacon.Api's own services (SignalR-backed notifiers, etc.). Keeps API-layer DI out
    /// of the host's Program.cs per §2.12. Realtime is on by default — see the overload taking
    /// <see cref="BeaconApiOptions"/> to turn it off or configure SignalR.
    /// </summary>
    public static IServiceCollection AddBeaconApiServices(this IServiceCollection services) =>
        services.AddBeaconApiServices(configure: null);

    /// <summary>
    /// Registers Beacon.Api's own services, including SignalR and the hub-backed
    /// <see cref="IApprovalNotifier"/> unless <see cref="BeaconApiOptions.Realtime"/> is turned off.
    /// Everything goes in through <c>TryAdd</c>, so a host that pre-registers its own implementation
    /// keeps it (§2.13).
    /// </summary>
    public static IServiceCollection AddBeaconApiServices(this IServiceCollection services, Action<BeaconApiOptions>? configure)
    {
        var options = new BeaconApiOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);

        // The current-user abstraction, required by ~28 Core handlers and services.
        // AddBeaconCookieAuthentication registers the same pair and KEEPS doing so — this is a
        // second, independent guarantee, not a move: a host that authenticates with JWT or API keys
        // never calls that method and would otherwise fail DI validation on all 28. Both call sites
        // use TryAdd and name the same implementation, so whichever runs first wins with the same
        // result. HttpContextUserContext is the real implementation (it lives in Beacon.Core), not
        // a stand-in, so this cannot shadow anything better via TryAdd's first-wins rule.
        // If you swap the implementation, change BOTH call sites.
        services.AddHttpContextAccessor();
        services.TryAddScoped<IBeaconUserContext, HttpContextUserContext>();

        if (!options.Realtime)
        {
            services.TryAddScoped<IApprovalNotifier, RealtimeDisabledApprovalNotifier>();

            return services;
        }

        var signalR = services.AddSignalR();
        options.ConfigureSignalR?.Invoke(signalR);

        services.TryAddSingleton<IUserIdProvider, HubUserIdProvider>();
        services.TryAddScoped<IApprovalNotifier, SignalRApprovalNotifier>();

        return services;
    }
}
