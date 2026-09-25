using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Core.Data;
using Beacon.Core.HostData;
using Beacon.Core.HostEndpoints;

namespace Beacon.MCP.HostEndpoints;

/// <summary>
/// The host's endpoint tools, discovered once from the built app's <see cref="EndpointDataSource"/> (endpoints do not
/// exist at DI time). The startup filter forces discovery as soon as the pipeline is built, so a misconfigured
/// <see cref="BeaconToolAttribute"/> fails startup instead of the first tool call.
/// </summary>
internal sealed class HostEndpointToolRegistry
{
    private readonly Lazy<IReadOnlyList<HostEndpointToolDescriptor>> _tools;
    private readonly HostEndpointToolOptions _options;

    public HostEndpointToolRegistry(IServiceProvider services, HostEndpointToolOptions options)
    {
        _options = options;
        _tools = new Lazy<IReadOnlyList<HostEndpointToolDescriptor>>(() => Discover(services), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<HostEndpointToolDescriptor> Tools => _tools.Value;

    public bool IsCatalogMode => Tools.Count > _options.NamedToolLimit;

    public void EnsureDiscovered() => _ = _tools.Value;

    /// <summary>A tool by its declared name or its <c>api_</c> tool name.</summary>
    public HostEndpointToolDescriptor? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Tools
            .Where(x => x.Name == name || x.ToolName == name)
            .FirstOrDefault();
    }

    private static IReadOnlyList<HostEndpointToolDescriptor> Discover(IServiceProvider services)
    {
        var endpoints = services.GetService<EndpointDataSource>()?.Endpoints ?? [];
        var apiDescriptions = services.GetService<IApiDescriptionGroupCollectionProvider>()?
            .ApiDescriptionGroups.Items
            .SelectMany(x => x.Items)
            .ToList() ?? [];
        var hasFallbackPolicy = services.GetService<IOptions<AuthorizationOptions>>()?.Value.FallbackPolicy != null;

        var discovery = new HostEndpointToolDiscovery(
            services.GetService<IXmlDocumentationProvider>() ?? NoXmlDocumentation.Instance,
            services.GetService<IServiceProviderIsService>());

        return discovery.Discover(endpoints, apiDescriptions, hasFallbackPolicy);
    }

    private sealed class NoXmlDocumentation : IXmlDocumentationProvider
    {
        public static readonly NoXmlDocumentation Instance = new();

        public string? GetTypeSummary(Type type) => null;

        public string? GetPropertySummary(Type declaringType, string propertyName) => null;
    }
}

/// <summary>Discovers the tools right after the host's pipeline (and so its endpoints) is built.</summary>
internal sealed class HostEndpointToolsStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            next(app);
            app.ApplicationServices.GetRequiredService<HostEndpointToolRegistry>().EnsureDiscovered();
        };
}

/// <summary>Resolves <see cref="HostEndpointToolOptions.ProjectName"/> to a project id.</summary>
internal interface IHostEndpointProjectResolver
{
    /// <summary>The project id, or null when no such project exists (the tools are then unavailable).</summary>
    Task<int?> ResolveAsync(CancellationToken cancellationToken);
}

internal sealed class HostEndpointProjectResolver(
    IDbContextFactory<BeaconContext> contextFactory,
    IMemoryCache cache,
    HostEndpointToolOptions options) : IHostEndpointProjectResolver
{
    private static readonly TimeSpan FoundLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MissingLifetime = TimeSpan.FromSeconds(30);

    public async Task<int?> ResolveAsync(CancellationToken cancellationToken)
    {
        var key = "beacon:host-endpoint-tools:project:" + options.ProjectName;
        if (cache.TryGetValue(key, out int? cached))
        {
            return cached;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projectId = await BuildQuery(context, options.ProjectName)
            .FirstOrDefaultAsync(cancellationToken);

        cache.Set(key, projectId, projectId.HasValue ? FoundLifetime : MissingLifetime);

        return projectId;
    }

    internal static IQueryable<int?> BuildQuery(BeaconContext context, string projectName) =>
        context.Projects
            .Where(x => x.Name == projectName)
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id);
}

internal sealed class HostEndpointToolCatalog(
    HostEndpointToolRegistry registry,
    IHostEndpointProjectResolver projectResolver) : IHostEndpointToolCatalog
{
    public async Task<HostEndpointToolListing?> GetForProjectAsync(int projectId, CancellationToken cancellationToken)
    {
        if (registry.Tools.Count == 0 || await projectResolver.ResolveAsync(cancellationToken) != projectId)
        {
            return null;
        }

        var tools = registry.Tools
            .Select(x => new HostEndpointToolSummary(x.Name, x.ToolName, x.FirstDescriptionLine()))
            .ToList();

        return new HostEndpointToolListing(tools, registry.IsCatalogMode);
    }
}
