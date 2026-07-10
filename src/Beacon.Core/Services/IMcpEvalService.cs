namespace Beacon.Core.Services;

/// <summary>
/// Runs the MCP text-to-SQL eval harness over the active golden cases. The interface lives in Core
/// (mirroring <see cref="IMcpLearningAggregationService"/> / <see cref="IEmbeddingIndexingService"/>);
/// the implementation lives in Beacon.AI and is wired at the composition root, so Core handlers and the
/// Core-level Hangfire job scheduler can start/run an eval without an AI reference (avoids the
/// AI → MCP cycle, §2.4). The AI implementation generates SQL and executes it strictly read-only,
/// applying <c>SqlReadOnlyAstValidator</c> + the query guardrail itself before every execution (§1.5).
/// </summary>
public interface IMcpEvalService
{
    /// <summary>
    /// Creates a new eval run row (status "Running") scoped to the optional project and returns its id.
    /// The actual case-by-case execution happens in <see cref="RunAsync"/>, typically enqueued as a
    /// background job so the API call returns immediately.
    /// </summary>
    Task<int> StartRunAsync(int? projectId, int? userId, CancellationToken ct);

    /// <summary>
    /// Executes the run: for each active golden case, generates SQL, executes both the gold and the
    /// generated SQL read-only, scores execution-accuracy by result-set fingerprint, assigns a failure
    /// tag, and (only when the eval judge is enabled) grades cosmetic-only diffs with a PII-redacted
    /// LLM judge. Persists one <c>McpEvalResult</c> per case and updates the run totals + status.
    /// </summary>
    Task RunAsync(int runId, CancellationToken ct);

    /// <summary>
    /// Read-only "does this one case pass?" probe used by the replay-verification gate
    /// (<see cref="IPatternReplayVerifier"/>). Generates SQL for the question against the data source's
    /// smart context — optionally with <paramref name="extraContext"/> appended (a candidate learned-pattern
    /// block) — then executes BOTH the gold and generated SQL strictly read-only through the SAME
    /// <c>SqlReadOnlyAstValidator</c> + guardrail path as the full run (no new execution path; §1.5) and
    /// compares result-set fingerprints. Generation runs at temperature 0 so a replay result is deterministic.
    /// Returns a <see cref="CaseEvaluation"/> carrying whether the case passed AND whether it was actually
    /// measurable (both gold and generated executed) — an infra/guardrail execution failure is NOT a clean
    /// pass/fail and must be excluded by the caller. Runs NO judge and performs NO persistence — it never
    /// mutates the data source or the eval store.
    /// </summary>
    Task<CaseEvaluation> EvaluateCasePassesAsync(
        int dataSourceId,
        string question,
        string goldSql,
        string? goldResultFingerprint,
        string? extraContext,
        CancellationToken ct);
}

/// <summary>
/// The outcome of a single read-only replay probe. <see cref="Passed"/> is whether the generated result
/// matched gold. <see cref="Measurable"/> is true only when BOTH the gold and generated SQL actually
/// executed — when false the pass/fail is meaningless (a transient DB/guardrail failure, not a clean
/// result) and the replay gate counts the case as errored rather than a flip or a regression.
/// </summary>
public readonly record struct CaseEvaluation(bool Passed, bool Measurable);
