using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.Core.Authorization;
using Beacon.Core.HostEndpoints;
using Beacon.Core.Mcp;
using Beacon.MCP.DynamicTools;

namespace Beacon.MCP.HostEndpoints;

public static class HostEndpointToolsServiceCollectionExtensions
{
    /// <summary>
    /// Exposes the host endpoints marked with <see cref="BeaconToolAttribute"/> (or <c>.WithBeaconTool(...)</c>) as
    /// MCP tools (read-only, plus write tools when <see cref="HostEndpointToolOptions.AllowWriteTools"/> is on) of the project <see cref="HostEndpointToolOptions.ProjectName"/>. Each call runs in-process
    /// through the endpoint's own binding, filters and authorization, as the principal
    /// <see cref="IMcpHostPrincipalFactory"/> builds for the MCP caller. Call after <c>AddBeaconMcp()</c>, once.
    /// Endpoints are discovered when the app's pipeline is built; a misconfigured marker fails startup.
    /// </summary>
    public static IServiceCollection AddHostEndpointTools(this IServiceCollection services, Action<HostEndpointToolOptions> configure)
    {
        if (services.Any(x => x.ServiceType == typeof(HostEndpointToolOptions)))
        {
            throw new InvalidOperationException("AddHostEndpointTools can only be called once.");
        }

        var options = new HostEndpointToolOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        // Core reads the request's caller through it; replaces only Core's off-request default, so a host principal stays.
        services.TryUseRequestPrincipal<Beacon.Shared.Http.HttpContextRequestPrincipal>();
        services.TryAddSingleton<IMcpHostPrincipalFactory, DefaultMcpHostPrincipalFactory>();
        services.TryAddSingleton<IHostEndpointProjectResolver, HostEndpointProjectResolver>();
        services.TryAddSingleton<HostEndpointToolRegistry>();
        services.TryAddSingleton<HostEndpointDispatcher>();
        services.TryAddTransient<HostEndpointToolService>();
        services.TryAddTransient<IHostEndpointToolOverview>(x => x.GetRequiredService<HostEndpointToolService>());
        services.AddTransient<IHostEndpointToolCatalog>(x => x.GetRequiredService<HostEndpointToolService>());
        services.AddTransient<IStartupFilter, HostEndpointToolsStartupFilter>();
        // Served through the SDK's list/call handlers beside the attribute tools, after the saved-query tools.
        services.AddMcpDynamicToolSource<HostEndpointDynamicToolSource>();

        return services;
    }

    /// <summary>
    /// Exposes a minimal API endpoint as the Beacon MCP tool <c>api_&lt;name&gt;</c>. <paramref name="readOnly"/>
    /// <c>false</c> declares a write tool, which fails startup unless <see cref="HostEndpointToolOptions.AllowWriteTools"/>
    /// is on.
    /// </summary>
    public static TBuilder WithBeaconTool<TBuilder>(this TBuilder builder, string name, string? description, bool readOnly)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new BeaconToolMetadata(name, description, readOnly));

        return builder;
    }

    /// <summary>
    /// Exposes a minimal API endpoint as the Beacon MCP tool <c>api_&lt;name&gt;</c>, with the write-tool hints.
    /// <paramref name="destructive"/> and <paramref name="idempotent"/> apply only when <paramref name="readOnly"/> is
    /// <c>false</c> (see <see cref="IBeaconToolMetadata.Destructive"/>, <see cref="IBeaconToolMetadata.Idempotent"/>).
    /// </summary>
    public static TBuilder WithBeaconTool<TBuilder>(this TBuilder builder, string name, string? description, bool readOnly, bool destructive, bool idempotent)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new BeaconToolMetadata(name, description, readOnly) { Destructive = destructive, Idempotent = idempotent });

        return builder;
    }
}
