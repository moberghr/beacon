using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        _tools = new Lazy<IReadOnlyList<HostEndpointToolDescriptor>>(() => Discover(services, options), LazyThreadSafetyMode.ExecutionAndPublication);
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

    private static IReadOnlyList<HostEndpointToolDescriptor> Discover(IServiceProvider services, HostEndpointToolOptions options)
    {
        var endpoints = services.GetService<EndpointDataSource>()?.Endpoints ?? [];
        var apiDescriptions = services.GetService<IApiDescriptionGroupCollectionProvider>()?
            .ApiDescriptionGroups.Items
            .SelectMany(x => x.Items)
            .ToList() ?? [];
        var hasFallbackPolicy = services.GetService<IOptions<AuthorizationOptions>>()?.Value.FallbackPolicy != null;
        var policyProvider = services.GetService<IAuthorizationPolicyProvider>();
        var warnings = new List<string>();

        var discovery = new HostEndpointToolDiscovery(
            services.GetService<IXmlDocumentationProvider>() ?? NoXmlDocumentation.Instance,
            services.GetService<IServiceProviderIsService>());

        var tools = discovery.Discover(
            endpoints,
            apiDescriptions,
            hasFallbackPolicy,
            policyProvider == null ? null : x => AuthenticationSchemesOf(x, policyProvider),
            options.TrustedAuthenticationSchemes,
            options.AllowWriteTools,
            options.CallerBindings.Keys.ToList(),
            options.DescriptionNote,
            options.AllowReadOnlyPost,
            warnings,
            services.GetService<ParameterPolicyFactory>());

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<HostEndpointToolRegistry>();
        foreach (var warning in warnings)
        {
            logger?.LogWarning("Beacon host endpoint tool not exposed: {Reason}", warning);
        }

        if (policyProvider != null)
        {
            WarnAboutAuthenticationOnlyEndpoints(logger, options, tools, policyProvider);
        }

        WarnAboutUnusedCallerBindings(services, options, tools);

        return tools;
    }

    /// <summary>
    /// An endpoint whose only requirement is an authenticated user (<c>[Authorize]</c> or <c>RequireAuthorization()</c>
    /// with no policy or roles, under the default policy, or such a fallback policy) admits every mapped MCP caller of
    /// the project. That may be intended, so it is a warning; an authorization filter on the endpoint is not seen here.
    /// A tool that binds an argument from the caller (<see cref="HostEndpointToolOptions.BindFromCaller(string, string)"/>)
    /// is the self-service pattern — any signed-in person, for their own records — and is not warned.
    /// </summary>
    private static void WarnAboutAuthenticationOnlyEndpoints(
        ILogger? logger,
        HostEndpointToolOptions options,
        IReadOnlyList<HostEndpointToolDescriptor> tools,
        IAuthorizationPolicyProvider policyProvider)
    {
        foreach (var tool in tools)
        {
            if (tool.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null || tool.CallerBindings.Count > 0)
            {
                continue;
            }

            var policy = HostEndpointDispatcher.CombinedPolicyAsync(tool.Endpoint.Metadata, policyProvider)
                .GetAwaiter()
                .GetResult();
            if (policy == null || !policy.Requirements.All(x => x is DenyAnonymousAuthorizationRequirement))
            {
                continue;
            }

            logger?.LogWarning(
                "Beacon host endpoint tool {Endpoint} [{Tool}] requires only an authenticated user (no policy, roles or other requirement): every mapped MCP caller of project {Project} can call it. Give it a policy or roles unless an endpoint filter checks permissions.",
                tool.Endpoint.DisplayName,
                tool.Name,
                options.ProjectName);
        }
    }

    // A binding no tool uses is most likely a typo or a renamed parameter, and that argument is then still the agent's to send.
    private static void WarnAboutUnusedCallerBindings(IServiceProvider services, HostEndpointToolOptions options, IReadOnlyList<HostEndpointToolDescriptor> tools)
    {
        var used = tools
            .SelectMany(x => x.CallerBindings)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unused = options.CallerBindings.Keys
            .Where(x => !used.Contains(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (unused.Count == 0)
        {
            return;
        }

        services.GetService<ILoggerFactory>()?
            .CreateLogger<HostEndpointToolRegistry>()
            .LogWarning(
                "HostEndpointToolOptions.BindFromCaller names {Arguments}, which no endpoint tool takes: check the spelling against the endpoints' parameters.",
                string.Join(", ", unused));
    }

    // The same combined policy HostEndpointDispatcher.AuthorizeAsync evaluates (minus requirement-only metadata, which
    // cannot name a scheme), resolved once at startup — hence the blocking wait on the policy provider.
    private static IReadOnlyCollection<string> AuthenticationSchemesOf(Endpoint endpoint, IAuthorizationPolicyProvider policyProvider)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            return [];
        }

        var policy = AuthorizationPolicy.CombineAsync(
                policyProvider,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
            .GetAwaiter()
            .GetResult();
        policy ??= policyProvider.GetFallbackPolicyAsync()
            .GetAwaiter()
            .GetResult();

        return policy?.AuthenticationSchemes ?? [];
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

/// <summary>
/// Resolves <see cref="HostEndpointToolOptions.ProjectName"/> to the host project's id through Core's shared
/// <see cref="IHostProjectResolver"/> (by host key, never by name), so the tools attach to the same project as the
/// host's data source and documents.
/// </summary>
internal interface IHostEndpointProjectResolver
{
    /// <summary>The project id, or null when no such project exists (the tools are then unavailable).</summary>
    Task<int?> ResolveAsync(CancellationToken cancellationToken);
}

internal sealed class HostEndpointProjectResolver(
    IHostProjectResolver hostProjects,
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

        var projectId = await hostProjects.FindAsync(options.ProjectName, cancellationToken);

        cache.Set(key, projectId, projectId.HasValue ? FoundLifetime : MissingLifetime);

        return projectId;
    }
}
