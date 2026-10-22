using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Authorization;
using Beacon.Core.HostEndpoints;
using Beacon.Core.Mcp;

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
        services.Configure<McpServerOptions>(InstallHandlers);

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

    /// <summary>
    /// Adds the endpoint tools through the SDK's list/call handlers, which serve tools that are not in the static
    /// tool collection: the eight attribute tools are untouched, and an already-installed handler is chained.
    /// </summary>
    private static void InstallHandlers(McpServerOptions options)
    {
        var previousList = options.Handlers.ListToolsHandler;
        var previousCall = options.Handlers.CallToolHandler;

        options.Handlers.ListToolsHandler = async (request, cancellationToken) =>
        {
            var result = previousList != null
                ? await previousList(request, cancellationToken)
                : new ListToolsResult();

            var service = request.Services?.GetService<HostEndpointToolService>();
            if (service == null)
            {
                return result;
            }

            var tools = await service.ListToolsAsync(cancellationToken);
            if (tools.Count > 0)
            {
                result.Tools = [.. result.Tools ?? [], .. tools];
            }

            return result;
        };

        options.Handlers.CallToolHandler = async (request, cancellationToken) =>
        {
            var name = request.Params?.Name;
            var service = request.Services?.GetService<HostEndpointToolService>();
            if (service != null && service.Handles(name))
            {
                return await service.CallAsync(name!, request.Params?.Arguments, cancellationToken);
            }

            if (previousCall != null)
            {
                return await previousCall(request, cancellationToken);
            }

            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        };
    }
}
