using System.Text.Json;
using Semantico.Core.Services;
using Semantico.MCP.Protocol;
using Semantico.MCP.Tools;

namespace Semantico.MCP.Services;

internal sealed class McpPlaygroundService(IEnumerable<IMcpTool> tools) : IMcpPlaygroundService
{
    public IReadOnlyList<string> ToolNames => tools.Select(t => t.Name).ToList();

    public async Task<McpPlaygroundResult> ExecuteToolAsync(
        string toolName, Dictionary<string, object?> arguments, int projectId, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => t.Name == toolName);
        if (tool == null)
            return new McpPlaygroundResult($"Unknown tool: {toolName}", true);

        var session = new McpClientSession
        {
            IsInitialized = true,
            ActiveProjectId = projectId,
            AllowedProjectIds = [projectId]
        };

        var json = JsonSerializer.Serialize(arguments);
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);

        var result = await tool.ExecuteAsync(jsonElement, session, ct);
        var text = string.Join("\n", result.Content.Select(c => c.Text ?? ""));
        return new McpPlaygroundResult(text, result.IsError);
    }
}
