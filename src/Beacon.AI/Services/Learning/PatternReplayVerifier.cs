using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.Learning;

/// <summary>
/// Replay-verification gate (§ Architecture ⑥). Measures whether injecting a candidate learned pattern
/// actually improves generation against the golden eval set, instead of trusting a confidence number.
/// It executes SQL ONLY through <see cref="IMcpEvalService.EvaluateCasePassesAsync"/>, which forces
/// read-only via <c>SqlReadOnlyAstValidator</c> + the query guardrail — no new execution path, so a
/// mutating candidate/gold SQL can never reach the provider (§1.5, lesson 2026-07-03). LLM generation runs
/// through the queue-backed provider inside the eval service (§6.1); this class holds no provider directly.
/// Mirrors the Core-interface / AI-impl split of <see cref="IMcpLearningAggregationService"/>.
/// </summary>
internal sealed class PatternReplayVerifier(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpEvalService evalService,
    ILogger<PatternReplayVerifier> logger) : IPatternReplayVerifier
{
    public async Task<ReplayVerdict> VerifyAsync(McpLearnedPattern candidate, int minFlips, CancellationToken ct)
    {
        List<RelevantCase> allCases;
        await using (var context = await contextFactory.CreateDbContextAsync(ct))
        {
            // Scope by data source in SQL; the table-reference match (case-insensitive substring on both
            // "Table" and "Schema.Table") is applied in memory since it can't translate reliably.
            allCases = await context.McpEvalCases
                .Where(x => x.IsActive)
                .Where(x => x.DataSourceId == candidate.DataSourceId)
                .Select(x =>
                    new RelevantCase
                    {
                        Question = x.Question,
                        GoldSql = x.GoldSql,
                        GoldResultFingerprint = x.GoldResultFingerprint
                    })
                .ToListAsync(ct);
        }

        var relevantCases = FilterByTableReference(allCases, candidate);

        // No golden evidence → never auto-approve on confidence alone (§ unwanted-behaviours).
        if (relevantCases.Count == 0)
        {
            return new ReplayVerdict(0, 0, 0, 0, 0, false);
        }

        var lessonBlock = BuildLessonBlock(candidate);

        var baselineFailing = 0;
        var flipped = 0;
        var regressions = 0;
        var errored = 0;

        foreach (var evalCase in relevantCases)
        {
            try
            {
                var baseline = await evalService.EvaluateCasePassesAsync(
                    candidate.DataSourceId, evalCase.Question, evalCase.GoldSql, evalCase.GoldResultFingerprint, null, ct);
                var candidateEval = await evalService.EvaluateCasePassesAsync(
                    candidate.DataSourceId, evalCase.Question, evalCase.GoldSql, evalCase.GoldResultFingerprint, lessonBlock, ct);

                // A case we could not actually measure — gold or generated failed to execute on either pass
                // (a transient DB/connection error or a guardrail/AST rejection, NOT a clean pass/fail) — is
                // counted as Errored and excluded. Otherwise an infra blip on the baseline would masquerade
                // as a flip (junk promotion) and one on the candidate pass as a regression (blocks a good
                // pattern). Neither side of such a case is trustworthy.
                if (!baseline.Measurable || !candidateEval.Measurable)
                {
                    errored++;
                    continue;
                }

                if (!baseline.Passed)
                {
                    baselineFailing++;
                }

                if (!baseline.Passed && candidateEval.Passed)
                {
                    flipped++;
                }
                else if (baseline.Passed && !candidateEval.Passed)
                {
                    regressions++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A case we could not measure counts as Errored (never a flip, never a regression), so a
                // flaky/erroring case can neither help a candidate over the promotion bar nor block it.
                errored++;
                logger.LogWarning(
                    ex,
                    "Replay verification threw for a case on pattern {PatternId} ({Schema}.{Table}); counting as errored",
                    candidate.Id, candidate.SchemaName, candidate.TableName);
            }
        }

        // Require at least one MEASURED case so an all-errored run (e.g. a DB outage flipping every case to
        // Errored) can never pass and is not byte-identical to "baseline already passes everything".
        var measured = relevantCases.Count - errored;
        var passed = flipped >= minFlips && regressions == 0 && measured > 0;
        return new ReplayVerdict(relevantCases.Count, baselineFailing, flipped, regressions, errored, passed);
    }

    // Relevant = gold SQL mentions the candidate's table. A candidate with no table (defensive — the
    // aggregation service always sets one) scopes to every active case for the data source.
    private static List<RelevantCase> FilterByTableReference(List<RelevantCase> cases, McpLearnedPattern candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.TableName))
        {
            return cases;
        }

        var qualified = $"{candidate.SchemaName}.{candidate.TableName}";
        return cases
            .Where(x =>
                x.GoldSql.Contains(candidate.TableName, StringComparison.OrdinalIgnoreCase)
                || x.GoldSql.Contains(qualified, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // The candidate lesson rendered as a prompt block, appended to the smart context during the candidate
    // replay pass. Mirrors KnowledgeGraphService.FormatLearnedPatternsForLlm's style (content + example).
    private static string BuildLessonBlock(McpLearnedPattern candidate)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Candidate Learned Pattern (under evaluation)");
        sb.AppendLine();
        sb.Append("- ").AppendLine(candidate.PatternContent);
        if (!string.IsNullOrEmpty(candidate.ExampleQuestion))
        {
            sb.Append("  \"").Append(candidate.ExampleQuestion).AppendLine("\"");
        }

        if (!string.IsNullOrEmpty(candidate.ExampleSql))
        {
            sb.Append("  → `").Append(candidate.ExampleSql).AppendLine("`");
        }

        return sb.ToString();
    }

    private sealed class RelevantCase
    {
        public string Question { get; init; } = null!;
        public string GoldSql { get; init; } = null!;
        public string? GoldResultFingerprint { get; init; }
    }
}
