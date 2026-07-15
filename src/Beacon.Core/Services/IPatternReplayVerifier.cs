using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services;

/// <summary>
/// Verifies a candidate learned pattern against the golden eval set by MEASURED replay (§ Architecture ⑥),
/// replacing the old "confidence ≥ threshold → auto-approve" heuristic. For each active golden case that is
/// relevant to the candidate (same data source, gold SQL references the candidate's table), the verifier
/// generates SQL twice through the eval harness's READ-ONLY path — once baseline, once with the candidate
/// lesson injected — and compares each to gold. A candidate is promoted ONLY if injecting it flips
/// enough baseline-failing cases to passing with ZERO regressions among relevant cases.
///
/// The interface lives in Core (mirroring <see cref="IMcpLearningAggregationService"/> /
/// <see cref="IMcpEvalService"/>); the implementation lives in Beacon.AI and is wired at the composition
/// root. It executes SQL exclusively through <see cref="IMcpEvalService.EvaluateCasePassesAsync"/>, which
/// forces read-only (<c>SqlReadOnlyAstValidator</c> + guardrail) — no new execution path (§1.5).
/// </summary>
public interface IPatternReplayVerifier
{
    /// <summary>
    /// Runs the replay measurement for <paramref name="candidate"/> and returns the verdict.
    /// <paramref name="minFlips"/> is the minimum number of baseline-failing relevant cases that injecting
    /// the candidate must flip to passing (with zero relevant regressions) for the verdict to pass.
    /// When there are no relevant golden cases, the verdict is <c>Passed = false</c> (never auto-approve on
    /// confidence alone). Cancellation propagates; other per-case errors are treated conservatively as
    /// not-improving.
    /// </summary>
    Task<ReplayVerdict> VerifyAsync(McpLearnedPattern candidate, int minFlips, CancellationToken ct);
}

/// <summary>
/// The measured outcome of a replay verification. <see cref="Errored"/> counts relevant cases that could
/// NOT be measured — an infra/guardrail execution failure on either the baseline or candidate pass, or a
/// thrown per-case error — as distinct from cases that were measured and simply did not improve.
/// <see cref="Passed"/> is true only when <see cref="Flipped"/> ≥ the configured minimum,
/// <see cref="Regressions"/> is zero, AND at least one relevant case was actually measured
/// (<c>RelevantCases - Errored &gt; 0</c>) — so an all-errored run (e.g. a DB outage) can never pass and is
/// distinguishable from "baseline already passes everything".
/// </summary>
public sealed record ReplayVerdict(int RelevantCases, int BaselineFailing, int Flipped, int Regressions, int Errored, bool Passed);
