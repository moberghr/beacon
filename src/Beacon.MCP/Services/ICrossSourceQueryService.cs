using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models;

namespace Beacon.MCP.Services;

internal interface ICrossSourceQueryService
{
    /// <summary>
    /// Generates + executes per-source SQL and joins the results in-memory. <paramref name="projectId"/>
    /// is the caller's AUTHORIZED project, threaded into per-source grounding so project-scoped context
    /// (glossary, golden cases) never leaks across projects sharing a data source. Within <paramref name="llmBudget"/>
    /// (null = not limited) the per-source generations and the join query must all fit before the first is made, and a
    /// repair is made only while the calls still to come stay covered.
    /// </summary>
    Task<(string Text, bool Succeeded)> ExecuteAsync(
        ILlmProvider llmProvider,
        List<RoutedSource> sources,
        int projectId,
        string question,
        McpSettingsData settings,
        bool execute,
        McpSignalBuilder signal,
        AskLlmBudget? llmBudget,
        CancellationToken ct);
}
