using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// Runs the MCP text-to-SQL eval harness over the active golden cases. The interface lives in Core
/// (mirroring <see cref="IMcpLearningAggregationService"/> / <see cref="IEmbeddingIndexingService"/>);
/// the implementation lives in Beacon.AI and is wired at the composition root, so Core handlers and the
/// Core-level job scheduler can start/run an eval without an AI reference (avoids the
/// AI → MCP cycle, §2.4). The AI implementation generates SQL and executes it strictly read-only,
/// applying <c>SqlReadOnlyAstValidator</c> + the query guardrail itself before every execution (§1.5).
/// </summary>
public interface IMcpEvalService
{
    /// <summary>
    /// Creates a new eval run row (status "Running") for the project and returns its id; a null
    /// <paramref name="projectId"/> is refused (<see cref="InvalidOperationException"/>) and creates nothing.
    /// <paramref name="trigger"/> is stored on the run: <see cref="McpEvalRunTrigger.Manual"/> for a run a person
    /// started (<paramref name="userId"/>), <see cref="McpEvalRunTrigger.Scheduled"/> for the nightly scheduled eval,
    /// which has no requester. The actual case-by-case execution happens in <see cref="RunAsync"/>, typically enqueued
    /// as a background job so the API call returns immediately.
    /// </summary>
    Task<int> StartRunAsync(int? projectId, int? userId, McpEvalRunTrigger trigger, CancellationToken ct);

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
    /// mutates the data source or the eval store. <paramref name="projectId"/> is the eval case's own
    /// ProjectId, threaded into smart-context assembly so project-scoped grounding (glossary, golden
    /// exemplars) never crosses projects sharing a data source.
    /// </summary>
    Task<CaseEvaluation> EvaluateCasePassesAsync(
        int dataSourceId,
        int projectId,
        string question,
        string goldSql,
        string? goldResultFingerprint,
        string? extraContext,
        CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="sql"/> once on the data source through the eval harness's own read-only executor — the
    /// <c>ISqlExecutionGate</c> with read-only forced and the row security context from <c>IRowSecurityResolver</c>, then the
    /// provider's read-only path, as an eval run executes a case's gold SQL (§1.5, §1.15; no new execution path) — and
    /// fingerprints the result as the eval does. Used before a golden case is promoted, so a case whose gold SQL does not run
    /// now is never created. A statement the gate refuses, one the data source answers with an error, and any failure on the
    /// way (an unreachable, disabled or missing data source, the statement timeout) did not run; only the caller's
    /// cancellation propagates. Runs no generation, no judge and stores nothing. <paramref name="projectId"/> picks the
    /// effective settings (row cap, statement timeout); without one the global settings apply.
    /// </summary>
    Task<GoldSqlReplay> ReplayGoldSqlAsync(int dataSourceId, int? projectId, string sql, CancellationToken ct);
}

/// <summary>
/// The outcome of running a gold SQL once (<see cref="IMcpEvalService.ReplayGoldSqlAsync"/>): whether it ran read-only, and
/// the fingerprint of its result when it did (null when it did not).
/// </summary>
public readonly record struct GoldSqlReplay(bool Ran, string? ResultFingerprint)
{
    public static GoldSqlReplay DidNotRun { get; } = new(false, null);
}

/// <summary>
/// The outcome of a single read-only replay probe. <see cref="Passed"/> is whether the generated result
/// matched gold. <see cref="Measurable"/> is true only when BOTH the gold and generated SQL actually
/// executed — when false the pass/fail is meaningless (a transient DB/guardrail failure, not a clean
/// result) and the replay gate counts the case as errored rather than a flip or a regression.
/// </summary>
public readonly record struct CaseEvaluation(bool Passed, bool Measurable);
