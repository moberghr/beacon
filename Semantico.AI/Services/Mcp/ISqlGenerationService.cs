using Semantico.AI.Services.LlmProviders;
using Semantico.Core.Models;

namespace Semantico.AI.Services.Mcp;

public record SqlGenerationResult(string Sql, List<string> TablesUsed);

public interface ISqlGenerationService
{
    Task<SqlGenerationResult> GenerateAsync(
        ILlmProvider llmProvider,
        string schemaContext,
        string question,
        McpSettingsData settings,
        CancellationToken ct);

    Task<string?> RetryWithErrorAsync(
        ILlmProvider llmProvider,
        string systemPrompt,
        string previousSql,
        string error,
        string fullContext,
        string? tablesContext,
        string question,
        CancellationToken ct);
}
