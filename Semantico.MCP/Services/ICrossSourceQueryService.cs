using Semantico.AI.Services.LlmProviders;
using Semantico.AI.Services.Mcp;
using Semantico.Core.Models;

namespace Semantico.MCP.Services;

internal interface ICrossSourceQueryService
{
    Task<string> ExecuteAsync(
        ILlmProvider llmProvider,
        List<RoutedSource> sources,
        string question,
        McpSettingsData settings,
        bool execute,
        CancellationToken ct);
}
