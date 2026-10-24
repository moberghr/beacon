using Beacon.Core.Configuration;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Api.Hubs;
using Beacon.Api.Security;
using Beacon.Api.SignalR;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Beacon.Api;

public static class ServiceConfiguration
{
    /// <summary>
    /// The antiforgery header Beacon's React shell sends its token in, which <see cref="AddApi"/> sets.
    /// </summary>
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>The antiforgery cookie name <see cref="AddApi"/> sets, kept apart from a default-named host cookie.</summary>
    public const string AntiforgeryCookieName = ".Beacon.Antiforgery";

    /// <summary>
    /// Adds Beacon's REST API for the React shell: its services (<c>AddBeaconApiServices</c>,
    /// configured by <paramref name="configure"/>), its authorization policies (<c>AddBeaconApiAuthorization</c>) and
    /// antiforgery with the header the shell sends (<see cref="AntiforgeryHeaderName"/>) and Beacon's cookie name
    /// (<see cref="AntiforgeryCookieName"/>). Both are set on the application's antiforgery options, which the host's own
    /// antiforgery shares. <c>MapBeacon()</c> maps the API.
    /// </summary>
    public static BeaconBuilder AddApi(this BeaconBuilder builder, Action<BeaconApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddBeaconApiServices(configure);
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddAntiforgery(x =>
        {
            x.HeaderName = AntiforgeryHeaderName;
            x.Cookie.Name = AntiforgeryCookieName;
        });

        return builder;
    }

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
        // result. HttpContextUserContext is the real implementation (it lives in Beacon.Api), not
        // a stand-in, so this cannot shadow anything better via TryAdd's first-wins rule.
        // If you swap the implementation, change BOTH call sites.
        services.AddHttpContextAccessor();
        services.TryAddScoped<IBeaconUserContext, HttpContextUserContext>();

        // Who acts, for the audit columns Beacon's endpoints fill: as AddBeaconCookieAuthentication registers it, so a host
        // that signs people in its own way (or embeds Beacon as an MCP gateway) records its administrators too.
        services.TryAddScoped<IActorUserResolver, ActorUserResolver>();

        // Core reads the request's caller through IBeaconRequestPrincipal; this replaces only Core's
        // off-request default, so a principal the host registered itself stays.
        services.TryUseRequestPrincipal<Beacon.Shared.Http.HttpContextRequestPrincipal>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, RequestPrincipalStartupCheck>());

        // Login throttle ships with Beacon.Api so every host gets it, realtime on or off.
        services.TryAddSingleton<LoginRateLimiter>();

        // Security headers and the Host/Origin check (UseBeaconAuthentication): Beacon:Security, unreadable origins refused at startup.
        services.AddOptions<BeaconHostSecurityOptions>()
            .BindConfiguration(BeaconHostSecurityOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BeaconHostSecurityOptions>, BeaconHostSecurityOptionsValidator>());

        // The per-caller limits of /beacon/api and of the anonymous requests Beacon refuses (Beacon:RateLimits).
        services.TryAddSingleton<BeaconApiRateLimiter>();
        // The embed routes' own per-minute limits (used only while embedding is on).
        services.TryAddSingleton<Beacon.Core.Services.Embed.EmbedAuditLimiter>();
        services.TryAddSingleton<EmbedRateLimiter>();

        if (!options.Realtime)
        {
            services.TryAddScoped<IApprovalNotifier, RealtimeDisabledApprovalNotifier>();

            return services;
        }

        // Hub bounds from Beacon:RequestLimits, registered before the host's ConfigureSignalR hook so a host's own
        // AddHubOptions (applied later) wins.
        services.AddOptions<HubOptions>()
            .Configure<IOptions<RequestLimitsOptions>>((hub, limits) =>
            {
                var value = limits.Value;
                if (value.HubMaxReceiveMessageBytes > 0)
                {
                    hub.MaximumReceiveMessageSize = value.HubMaxReceiveMessageBytes;
                }

                if (value.HubMaxParallelInvocationsPerClient > 0)
                {
                    hub.MaximumParallelInvocationsPerClient = value.HubMaxParallelInvocationsPerClient;
                }

                if (value.HubClientTimeoutSeconds > 0)
                {
                    hub.ClientTimeoutInterval = TimeSpan.FromSeconds(value.HubClientTimeoutSeconds);
                }
            });

        var signalR = services.AddSignalR();
        options.ConfigureSignalR?.Invoke(signalR);

        UseHubUserIdProvider(services);
        services.TryAddScoped<IApprovalNotifier, SignalRApprovalNotifier>();

        return services;
    }

    // AddSignalR registers SignalR's own provider with TryAdd, so a TryAdd here would never win. Beacon's provider replaces
    // that default (also when the host called AddSignalR first, and a later AddSignalR keeps it); one the host registered
    // itself stays.
    private static void UseHubUserIdProvider(IServiceCollection services)
    {
        var registered = services.LastOrDefault(x => x.ServiceType == typeof(IUserIdProvider));
        if (registered != null && registered.ImplementationType != typeof(DefaultUserIdProvider))
        {
            return;
        }

        services.RemoveAll<IUserIdProvider>();
        services.AddSingleton<IUserIdProvider, HubUserIdProvider>();
    }
}
