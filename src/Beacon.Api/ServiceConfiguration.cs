using Beacon.Api.Hubs;
using Beacon.Core.Services;

namespace Beacon.Api;

public static class ServiceConfiguration
{
    /// <summary>
    /// Registers Beacon.Api's own services (SignalR-backed notifiers, etc.). Keeps API-layer DI out
    /// of the host's Program.cs per §2.12.
    /// </summary>
    public static IServiceCollection AddBeaconApiServices(this IServiceCollection services)
    {
        services.AddScoped<IApprovalNotifier, SignalRApprovalNotifier>();

        return services;
    }
}
