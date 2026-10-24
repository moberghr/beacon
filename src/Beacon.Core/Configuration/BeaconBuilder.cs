using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Core;

/// <summary>
/// Builder for configuring Beacon services with a database provider.
/// </summary>
public class BeaconBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    /// <summary>
    /// The configuration <c>AddBeacon</c> registered, which the chain's later steps complete (<c>AddAI()</c> sets
    /// <see cref="BeaconConfiguration.UseAI"/>). Null on a builder made without it.
    /// </summary>
    internal BeaconConfiguration? Options { get; }

    /// <summary>Set by <c>AddAI()</c>: Beacon's AI services are registered through this builder.</summary>
    internal bool AiAdded { get; set; }

    /// <summary>
    /// Marks the services of a host that uses the chain (<c>AddBeacon</c>, <c>AddAI</c>, <c>AddMcp</c>): the older
    /// <c>AddBeaconAI</c> and <c>AddBeaconMcp</c> refuse to run beside it, so the two ways never register Beacon's AI or
    /// MCP services twice or leave the AI-off stand-ins in place unseen.
    /// </summary>
    internal static void MarkChain(IServiceCollection services)
    {
        services.TryAddSingleton<BeaconHostChainMarker>();
    }

    /// <summary>Whether <see cref="MarkChain"/> ran on <paramref name="services"/>.</summary>
    internal static bool UsesChain(IServiceCollection services)
    {
        return services.Any(x => x.ServiceType == typeof(BeaconHostChainMarker));
    }

    internal BeaconBuilder(IServiceCollection services, IConfiguration configuration)
        : this(services, configuration, null)
    {
    }

    internal BeaconBuilder(IServiceCollection services, IConfiguration configuration, BeaconConfiguration? options)
    {
        Services = services;
        Configuration = configuration;
        Options = options;
    }
}

/// <summary>The service <see cref="BeaconBuilder.MarkChain"/> registers.</summary>
internal sealed class BeaconHostChainMarker;
