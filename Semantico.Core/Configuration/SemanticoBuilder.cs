using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Core;

/// <summary>
/// Builder for configuring Semantico services with a database provider.
/// </summary>
public class SemanticoBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    internal SemanticoBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }
}
