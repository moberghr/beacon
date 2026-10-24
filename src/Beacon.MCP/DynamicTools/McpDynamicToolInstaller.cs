using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.MCP.Services;

namespace Beacon.MCP.DynamicTools;

/// <summary>
/// Serves every registered <see cref="IMcpDynamicToolSource"/> through one pair of SDK list/call handlers. Both entry
/// points (<c>AddBeaconMcp</c> for the saved-query tools, <c>AddHostEndpointTools</c>) call it; the handlers and the
/// startup name check are installed once however many sources register.
/// </summary>
internal static class McpDynamicToolInstaller
{
    public static IServiceCollection AddMcpDynamicToolSource<TSource>(this IServiceCollection services)
        where TSource : class, IMcpDynamicToolSource
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpDynamicToolSource, TSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McpServerOptions>, McpDynamicToolHandlers>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, McpDynamicToolNameCheck>());

        return services;
    }

    /// <summary>
    /// Fails when a tool name is claimed twice: by two sources, or by a source and an attribute tool. Without the check
    /// one of them would silently win the name.
    /// </summary>
    internal static void EnsureNoNameClash(IReadOnlyList<McpDynamicToolNames> claims, IReadOnlyCollection<string> attributeToolNames)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                EnsureDisjoint(claims[i], claims[j]);
            }
        }

        foreach (var name in attributeToolNames)
        {
            var claim = claims
                .Where(x => x.Claims(name))
                .FirstOrDefault();
            if (claim != null)
            {
                throw Clash(name, "the attribute tools", claim.Source);
            }
        }
    }

    private static void EnsureDisjoint(McpDynamicToolNames first, McpDynamicToolNames second)
    {
        var clashingName = first.Names
            .Where(second.Claims)
            .Concat(second.Names.Where(first.Claims))
            .FirstOrDefault();
        if (clashingName != null)
        {
            throw Clash(clashingName, first.Source, second.Source);
        }

        foreach (var prefix in first.Prefixes)
        {
            var overlapping = second.Prefixes
                .Where(x => x.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(x, StringComparison.Ordinal))
                .FirstOrDefault();
            if (overlapping != null)
            {
                throw Clash(prefix.Length >= overlapping.Length ? prefix + "*" : overlapping + "*", first.Source, second.Source);
            }
        }
    }

    private static InvalidOperationException Clash(string name, string first, string second) =>
        new($"The MCP tool name '{name}' is claimed by both {first} and {second}. Each tool name must be served by one source: rename one of them.");
}

/// <summary>
/// The list/call handlers: the sources' tools are listed after the tools of any handler installed before, in source
/// order; a call goes to the first source that serves the name, else to the handler installed before.
/// </summary>
internal sealed class McpDynamicToolHandlers(IEnumerable<IMcpDynamicToolSource> sources) : IConfigureOptions<McpServerOptions>
{
    private readonly IReadOnlyList<IMcpDynamicToolSource> _sources = sources
        .OrderBy(x => x.Order)
        .ThenBy(x => x.GetType().FullName, StringComparer.Ordinal)
        .ToList();

    public void Configure(McpServerOptions options)
    {
        var previousList = options.Handlers.ListToolsHandler;
        var previousCall = options.Handlers.CallToolHandler;

        options.Handlers.ListToolsHandler = async (request, cancellationToken) =>
        {
            var result = previousList != null
                ? await previousList(request, cancellationToken)
                : new ListToolsResult();

            var services = request.Services;
            if (services == null)
            {
                return result;
            }

            var tools = new List<Tool>();
            foreach (var source in _sources)
            {
                ThrowIfAccessUndetermined(services);
                tools.AddRange(await source.ListToolsAsync(services, cancellationToken));
            }

            ThrowIfAccessUndetermined(services);
            if (tools.Count > 0)
            {
                result.Tools = [.. result.Tools ?? [], .. tools];
            }

            return result;
        };

        options.Handlers.CallToolHandler = async (request, cancellationToken) =>
        {
            var name = request.Params?.Name;
            var services = request.Services;
            if (services != null && !string.IsNullOrEmpty(name))
            {
                var source = _sources
                    .Where(x => x.Handles(services, name))
                    .FirstOrDefault();
                if (source != null)
                {
                    return await source.CallAsync(services, name, request.Params?.Arguments, cancellationToken);
                }
            }

            if (previousCall != null)
            {
                return await previousCall(request, cancellationToken);
            }

            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        };
    }

    // A tool list never comes back shorter because access was undetermined: it fails (§1.13).
    private static void ThrowIfAccessUndetermined(IServiceProvider services)
    {
        if (services.GetService<McpProjectContext>()?.AccessUndetermined == true)
        {
            throw new McpProtocolException(ProjectContextFactory.AccessUndeterminedMessage, McpErrorCode.InternalError);
        }
    }
}

/// <summary>Checks the claimed tool names once the app's pipeline (and so the host's endpoint tools) is built.</summary>
internal sealed class McpDynamicToolNameCheck(IEnumerable<IMcpDynamicToolSource> sources) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            next(app);

            var attributeToolNames = app.ApplicationServices.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection?
                .Select(x => x.ProtocolTool.Name)
                .ToList() ?? [];
            var claims = sources
                .Select(x => x.ClaimedNames())
                .ToList();

            McpDynamicToolInstaller.EnsureNoNameClash(claims, attributeToolNames);
        };
}
