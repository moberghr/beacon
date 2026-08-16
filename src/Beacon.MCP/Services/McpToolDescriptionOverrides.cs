using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.MCP.Services;

/// <summary>
/// Applies admin-configured per-tool description overrides (from <see cref="McpSettingsData"/>) to a
/// tools/list response. A non-empty override replaces the compiled <c>[Description]</c> text; an
/// unset / whitespace override keeps the original. Wired via the list-tools request filter created by
/// <see cref="CreateListToolsFilter"/> and registered in <c>ServiceConfiguration</c>, so it composes
/// with attribute-discovered tools.
/// The SDK exposes <see cref="Tool"/> instances as process-wide singletons
/// (<c>AIFunctionMcpServerTool.ProtocolTool</c>), so an override NEVER mutates the instance —
/// the list element is replaced with a per-response clone carrying the override text.
/// </summary>
internal static class McpToolDescriptionOverrides
{
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter()
    {
        return next => async (request, cancellationToken) =>
        {
            // Admin per-tool description overrides (McpSettingsData) — a non-empty override
            // replaces the compiled [Description] in tools/list; settings reads are cached 5 min.
            // Overrides are cosmetic: a missing settings provider or a failing settings read must
            // never break tools/list — fall through and serve the compiled descriptions.
            var result = await next(request, cancellationToken);
            try
            {
                var settingsProvider = request.Services?.GetService<IMcpSettingsProvider>();
                if (settingsProvider != null)
                {
                    var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
                    Apply(result.Tools, settings);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var logger = request.Services?
                    .GetService<ILoggerFactory>()?
                    .CreateLogger(typeof(McpToolDescriptionOverrides));
                logger?.LogWarning(ex, "Failed to apply MCP tool description overrides; serving compiled descriptions.");
            }

            return result;
        };
    }

    public static void Apply(IList<Tool>? tools, McpSettingsData settings)
    {
        if (tools == null)
        {
            return;
        }

        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            var overrideDescription = GetOverride(tool.Name, settings);
            if (string.IsNullOrWhiteSpace(overrideDescription))
            {
                continue;
            }

            // Clone copies every property SDK 2.2's Tool carries so nothing is dropped from the wire.
            tools[i] = new Tool
            {
                Name = tool.Name,
                Title = tool.Title,
                Description = overrideDescription,
                InputSchema = tool.InputSchema,
                OutputSchema = tool.OutputSchema,
                Annotations = tool.Annotations,
                Icons = tool.Icons,
                Meta = tool.Meta
            };
        }
    }

    private static string? GetOverride(string toolName, McpSettingsData settings)
    {
        return toolName switch
        {
            "get_context" => settings.GetContextDescription,
            "search" => settings.SearchDescription,
            "get_documentation" => settings.GetDocumentationDescription,
            "query" => settings.QueryDescription,
            "ask" => settings.AskDescription,
            _ => null
        };
    }
}
