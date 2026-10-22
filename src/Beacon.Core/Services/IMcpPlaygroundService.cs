namespace Beacon.Core.Services;

public record McpPlaygroundResult(string Text, bool IsError);

public interface IMcpPlaygroundService
{
    /// <summary>The built-in MCP tools the host serves, by name; a tool the host left out is not listed.</summary>
    IReadOnlyList<string> ToolNames { get; }
    Task<McpPlaygroundResult> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments, int projectId, CancellationToken ct);
}
