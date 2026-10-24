using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
