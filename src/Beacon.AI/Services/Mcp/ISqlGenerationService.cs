using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

/// <summary>
/// <paramref name="Assumptions"/> and <paramref name="ClarificationHint"/> are parsed from a leading
/// <c>-- assumptions:</c> / <c>-- clarification:</c> comment block the model may emit before the SQL
/// (spec item 3). Both default to <c>null</c> so <c>new SqlGenerationResult(sql, tables)</c> still
/// compiles; callers that need a non-null view use <c>Assumptions ?? []</c>.
/// </summary>
public record SqlGenerationResult(
    string Sql,
    List<string> TablesUsed,
    IReadOnlyList<string>? Assumptions = null,
    string? ClarificationHint = null);

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
