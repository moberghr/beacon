namespace Beacon.Core.Services;

public record McpPlaygroundResult(string Text, bool IsError);

public interface IMcpPlaygroundService
{
    IReadOnlyList<string> ToolNames { get; }
    Task<McpPlaygroundResult> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments, int projectId, CancellationToken ct);
}
