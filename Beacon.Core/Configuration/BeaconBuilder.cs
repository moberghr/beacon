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

    internal BeaconBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }
}
