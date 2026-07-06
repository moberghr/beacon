using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

public interface IKnowledgeAnswerService
{
    Task<string> AnswerAsync(
        ILlmProvider llmProvider,
        int projectId,
        string question,
        McpSettingsData settings,
        CancellationToken ct);
}
