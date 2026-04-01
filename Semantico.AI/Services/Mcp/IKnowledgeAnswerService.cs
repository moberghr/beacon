using Semantico.AI.Services.LlmProviders;
using Semantico.Core.Models;

namespace Semantico.AI.Services.Mcp;

public interface IKnowledgeAnswerService
{
    Task<string> AnswerAsync(
        ILlmProvider llmProvider,
        int projectId,
        string question,
        McpSettingsData settings,
        CancellationToken ct);
}
