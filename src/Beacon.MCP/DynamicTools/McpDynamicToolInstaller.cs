using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.MCP.Services;

namespace Beacon.MCP.DynamicTools;

/// <summary>
/// Serves an <see cref="IMcpDynamicToolSource"/> through the SDK's list/call handlers. Both entry points
/// (<c>AddBeaconMcp</c> for the saved-query tools, <c>AddHostEndpointTools</c>) call it. Each source's handlers are
/// installed once, at the point of its first registration, chaining the handlers installed before it: the source
/// registered later lists its tools after the earlier ones and sees a call first, and a host's own handler keeps its
/// place between them. The startup name check is installed once.
/// </summary>
internal static class McpDynamicToolInstaller
{
    public static IServiceCollection AddMcpDynamicToolSource<TSource>(this IServiceCollection services)
        where TSource : class, IMcpDynamicToolSource
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpDynamicToolSource, TSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McpServerOptions>, McpDynamicToolHandlers<TSource>>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, McpDynamicToolNameCheck>());

        return services;
    }

    /// <summary>
    /// Fails when two sources claim the same tool name or overlapping name prefixes: one of them would otherwise
    /// silently win the name.
    /// </summary>
    internal static void EnsureNoNameClash(IReadOnlyList<McpDynamicToolNames> claims)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                EnsureDisjoint(claims[i], claims[j]);
            }
        }
    }

    /// <summary>
    /// The attribute tools whose names a source also claims, with that source. The SDK answers such a name with the
    /// attribute tool (it looks the name up in its tool collection before any handler runs), as it did before the
    /// sources shared one installer, so these are reported, not refused.
    /// </summary>
    internal static IReadOnlyList<(string ToolName, string Source)> AttributeToolsClaimedBySources(
        IReadOnlyList<McpDynamicToolNames> claims,
        IReadOnlyCollection<string> attributeToolNames)
    {
        var shadowed = new List<(string, string)>();
        foreach (var name in attributeToolNames)
        {
            var claim = claims
                .Where(x => x.Claims(name))
                .FirstOrDefault();
            if (claim != null)
            {
                shadowed.Add((name, claim.Source));
            }
        }

        return shadowed;
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
/// One source's list/call handlers: its tools are listed after those of the handlers installed before, and a call it
/// serves goes to it, any other to the handler installed before.
/// </summary>
internal sealed class McpDynamicToolHandlers<TSource>(IEnumerable<IMcpDynamicToolSource> sources) : IConfigureOptions<McpServerOptions>
    where TSource : class, IMcpDynamicToolSource
{
    private readonly IMcpDynamicToolSource _source = sources
        .OfType<TSource>()
        .First();

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

            ThrowIfAccessUndetermined(services);
            var tools = await _source.ListToolsAsync(services, cancellationToken);
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
            if (services != null && !string.IsNullOrEmpty(name) && _source.Handles(services, name))
            {
                return await _source.CallAsync(services, name, request.Params?.Arguments, cancellationToken);
            }

            if (previousCall != null)
            {
                return await previousCall(request, cancellationToken);
            }

            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        };
    }

    // Access that could not be determined fails the tool list rather than leaving this source's tools out (§1.13).
    private static void ThrowIfAccessUndetermined(IServiceProvider services)
    {
        if (services.GetService<McpProjectContext>()?.AccessUndetermined == true)
        {
            throw new McpProtocolException(ProjectContextFactory.AccessUndeterminedMessage, McpErrorCode.InternalError);
        }
    }
}

/// <summary>
/// Checks the claimed tool names once the app's pipeline (and so the host's endpoint tools) is built: two sources
/// claiming one name fail startup; an attribute tool a source also claims is logged as a warning.
/// </summary>
internal sealed class McpDynamicToolNameCheck(IEnumerable<IMcpDynamicToolSource> sources) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            next(app);

            var services = app.ApplicationServices;
            var claims = sources
                .Select(x => x.ClaimedNames(services))
                .ToList();
            McpDynamicToolInstaller.EnsureNoNameClash(claims);

            var attributeToolNames = services.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection?
                .Select(x => x.ProtocolTool.Name)
                .ToList() ?? [];
            var shadowed = McpDynamicToolInstaller.AttributeToolsClaimedBySources(claims, attributeToolNames);
            if (shadowed.Count == 0)
            {
                return;
            }

            var logger = services.GetService<ILoggerFactory>()?.CreateLogger<McpDynamicToolNameCheck>();
            foreach (var (toolName, source) in shadowed)
            {
                logger?.LogWarning(
                    "The MCP attribute tool {ToolName} has a name {Source} also claim; calls to it reach the attribute tool. Rename it to keep the names apart.",
                    toolName,
                    source);
            }
        };
}
