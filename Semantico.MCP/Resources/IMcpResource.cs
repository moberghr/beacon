using Semantico.MCP.Protocol;

namespace Semantico.MCP.Resources;

internal interface IMcpResource
{
    bool CanHandle(string uri);
    Task<List<McpResource>> ListAsync(CancellationToken ct);
    Task<McpResourceContent?> ReadAsync(string uri, CancellationToken ct);
}
