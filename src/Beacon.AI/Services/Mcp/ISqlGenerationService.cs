using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

public record SqlGenerationResult(string Sql, List<string> TablesUsed);

public interface ISqlGenerationService
{
    /// <summary>
    /// Generates a single SQL query. <paramref name="temperature"/> overrides the sampling temperature when
    /// supplied (defaults to <c>0.1</c> when null) — the replay-verification gate passes <c>0.0</c> so both
    /// the baseline and candidate generations are deterministic and a "flip" cannot be sampling noise.
    /// </summary>
    Task<SqlGenerationResult> GenerateAsync(
        ILlmProvider llmProvider,
        string schemaContext,
        string question,
        McpSettingsData settings,
        CancellationToken ct,
        decimal? temperature = null);

    /// <summary>
    /// Generates multiple candidate SQL queries for the same question by sampling at a non-zero
    /// temperature. Used by self-consistency voting. Each sample goes through the LLM sequentially
    /// (per §6.1 the provider funnels through <c>LlmRequestQueue</c>). Unusable samples (truncated /
    /// no SQL) are dropped, so the returned list may contain fewer than <paramref name="candidateCount"/>
    /// entries.
    /// </summary>
    Task<IReadOnlyList<SqlGenerationResult>> GenerateCandidatesAsync(
        ILlmProvider llmProvider,
        string schemaContext,
        string question,
        McpSettingsData settings,
        int candidateCount,
        decimal temperature,
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
