using System.Text.Json;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    object InputSchema { get; }
    Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct);
}
