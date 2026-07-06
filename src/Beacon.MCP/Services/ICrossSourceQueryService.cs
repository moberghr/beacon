using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models;

namespace Beacon.MCP.Services;

internal interface ICrossSourceQueryService
{
    Task<(string Text, bool Succeeded)> ExecuteAsync(
        ILlmProvider llmProvider,
        List<RoutedSource> sources,
        string question,
        McpSettingsData settings,
        bool execute,
        McpSignalBuilder signal,
        CancellationToken ct);
}
