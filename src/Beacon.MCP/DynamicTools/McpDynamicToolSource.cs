using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Beacon.MCP.DynamicTools;

/// <summary>
/// A source of the MCP tools served through the SDK's list/call handlers beside the attribute tools (the saved-query
/// tools, the host endpoint tools): it lists the tools the current caller sees and calls one by name. A source is a
/// singleton; the per-request work resolves from the request's services. Register one with
/// <see cref="McpDynamicToolInstaller.AddMcpDynamicToolSource{TSource}"/>.
/// </summary>
internal interface IMcpDynamicToolSource
{
    /// <summary>The source's place in <c>tools/list</c> and in call routing: lower first.</summary>
    int Order { get; }

    /// <summary>
    /// The tool names the source can answer, as far as they are known once the app's pipeline is built: its fixed
    /// names, the prefixes of the names it generates and the generated names it already knows. Two sources (or a source
    /// and an attribute tool) claiming the same name fail startup.
    /// </summary>
    McpDynamicToolNames ClaimedNames();

    /// <summary>Whether the source serves <paramref name="toolName"/> for this request.</summary>
    bool Handles(IServiceProvider requestServices, string toolName);

    /// <summary>
    /// The tools the caller sees. A source that cannot determine the caller's access throws, or marks the request's
    /// project context undetermined; it never answers with fewer tools (§1.13).
    /// </summary>
    Task<IReadOnlyList<Tool>> ListToolsAsync(IServiceProvider requestServices, CancellationToken cancellationToken);

    /// <summary>Calls a tool <see cref="Handles"/> accepted; the source audits every outcome (§1.7).</summary>
    Task<CallToolResult> CallAsync(IServiceProvider requestServices, string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken);
}

/// <summary>The tool names a <see cref="IMcpDynamicToolSource"/> claims: exact names and name prefixes.</summary>
internal sealed record McpDynamicToolNames(string Source, IReadOnlyCollection<string> Names, IReadOnlyCollection<string> Prefixes)
{
    public bool Claims(string toolName) =>
        Names.Contains(toolName, StringComparer.Ordinal)
        || Prefixes.Any(x => toolName.StartsWith(x, StringComparison.Ordinal));
}

/// <summary>The search/run pair a dynamic source lists in place of its tools when it has too many to name.</summary>
internal static class McpCatalogTool
{
    public static Tool Create(string name, string title, string description, JsonElement schema, ToolAnnotations annotations) =>
        new()
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = schema,
            Annotations = annotations
        };

    public static ToolAnnotations ReadOnlyAnnotations() =>
        new()
        {
            ReadOnlyHint = true,
            DestructiveHint = false,
            IdempotentHint = true,
            OpenWorldHint = false
        };
}
