using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.Eval;

/// <summary>
/// Executes the MCP text-to-SQL eval harness (§ Architecture ①). For each active golden case it
/// generates SQL through the AI pipeline, then executes BOTH the gold and the generated SQL strictly
/// read-only via the Core-level <see cref="IDataSourceProviderFactory"/> — applying
/// <see cref="SqlReadOnlyAstValidator"/> + <see cref="IQueryGuardrailService"/> ITSELF before every
/// execution (read-only is NOT inherited from any wrapper; §1.5, lesson 2026-07-03). Execution-accuracy
/// is scored by comparing result-set fingerprints, and a failure tag distinguishes retrieval from
/// SQL-reasoning from execution failures. The LLM-as-judge is invoked ONLY when
/// <c>EnableEvalJudge</c> is true and, when invoked, sees only a PII-redacted representation (§1.6/§1.11).
/// Mirrors the <see cref="IMcpLearningAggregationService"/> split: interface in Core, impl here, wired
/// at the composition root; one context / one SaveChanges per unit of work (§5.7).
/// </summary>
internal sealed class McpEvalService(
    IDbContextFactory<BeaconContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph,
    ISqlGenerationService sqlGenerationService,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    IMcpSettingsProvider settingsProvider,
    ILlmProvider llmProvider,
    ILogger<McpEvalService> logger) : IMcpEvalService
{
    // Hard cap on rows handed to the (opt-in, PII-redacted) judge so a large result set never balloons
    // the prompt — the judge only needs a structural sample to spot a cosmetic-only difference.
    private const int MaxJudgeRows = 20;

    // Per-statement execution ceiling, matching the MCP query executor.
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);

    public async Task<int> StartRunAsync(int? projectId, int? userId, CancellationToken ct)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var run = new McpEvalRun
        {
            ProjectId = projectId,
            TriggeredByUserId = userId,
            Status = "Running",
            JudgeEnabled = settings.EnableEvalJudge
        };

        context.McpEvalRuns.Add(run);
        await context.SaveChangesAsync(ct);

        return run.Id;
    }

    public async Task RunAsync(int runId, CancellationToken ct)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var run = await context.McpEvalRuns
            .Where(x => x.Id == runId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Eval run {runId} not found.");

        run.JudgeEnabled = settings.EnableEvalJudge;

        try
        {
            var casesQuery = context.McpEvalCases
                .Where(x => x.IsActive);

            if (run.ProjectId.HasValue)
            {
                casesQuery = casesQuery.Where(x => x.ProjectId == run.ProjectId.Value);
            }

            var cases = await casesQuery
                .OrderBy(x => x.Id)
                .Select(x =>
                    new EvalCaseData
                    {
                        Id = x.Id,
                        DataSourceId = x.DataSourceId,
                        Question = x.Question,
                        GoldSql = x.GoldSql,
                        GoldResultFingerprint = x.GoldResultFingerprint
                    })
                .ToListAsync(ct);

            var dataSourceIds = cases
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToList();

            var dataSources = await context.DataSources
                .Where(x => dataSourceIds.Contains(x.Id))
                .ToListAsync(ct);

            var dataSourcesById = dataSources.ToDictionary(x => x.Id);

            var passed = 0;
            var errored = 0;
            foreach (var evalCase in cases)
            {
                var result = await EvaluateCaseAsync(runId, evalCase, dataSourcesById, settings, ct);
                if (result.Passed)
                {
                    passed++;
                }
                else if (result.FailureTag == McpEvalFailureTag.HarnessError)
                {
                    errored++;
                }

                context.McpEvalResults.Add(result);
            }

            // Execution accuracy is scored ONLY over cases that were actually evaluated — harness/infra
            // failures (LLM outage, DB blip, missing data source) are excluded so a mid-run outage does not
            // silently deflate the headline metric. The excluded count is surfaced on the run.
            var evaluated = cases.Count - errored;
            run.TotalCases = cases.Count;
            run.PassedCases = passed;
            run.ExecutionAccuracy = evaluated > 0 ? (double)passed / evaluated : 0.0;
            run.Status = "Completed";
            if (errored > 0)
            {
                run.Notes = $"{errored} of {cases.Count} case(s) could not be evaluated (harness/infrastructure error) and are excluded from execution accuracy.";
            }

            await context.SaveChangesAsync(ct);

            logger.LogInformation(
                "Eval run {RunId} completed: {Passed}/{Evaluated} evaluated cases passed (EX {Accuracy:P1}); {Errored} not evaluated (harness error)",
                runId, passed, evaluated, run.ExecutionAccuracy, errored);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Eval run {RunId} failed", runId);
            run.Status = "Failed";
            run.Notes = ex.Message;
            await context.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<McpEvalResult> EvaluateCaseAsync(
        int runId,
        EvalCaseData evalCase,
        IReadOnlyDictionary<int, DataSource> dataSourcesById,
        McpSettingsData settings,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!dataSourcesById.TryGetValue(evalCase.DataSourceId, out var dataSource))
            {
                return FailedResult(runId, evalCase.Id, null, McpEvalFailureTag.HarnessError,
                    $"Data source {evalCase.DataSourceId} not found.", sw);
            }

            // Generate + execute (gold & generated) + fingerprint-compare — the reusable read-only core,
            // here with no extra context (behaviour-preserving). The candidate-lesson replay path calls the
            // SAME method with an extraContext suffix. Normal runs keep the default sampling temperature
            // (null → 0.1) so headline eval behaviour is unchanged; only replay forces temperature 0.
            var outcome = await GenerateExecuteCompareAsync(
                dataSource, evalCase.Question, evalCase.GoldSql, evalCase.GoldResultFingerprint, null, settings,
                generationTemperature: null, ct);

            var failureTag = DetermineFailureTag(
                outcome.Passed, outcome.GeneratedTables, outcome.RelevantTables, outcome.GoldExec, outcome.GeneratedExec);
            var executionError = outcome.GeneratedExec.Success ? outcome.GoldExec.ErrorMessage : outcome.GeneratedExec.ErrorMessage;

            var judgeUsed = false;
            string? judgeVerdict = null;

            // OPTIONAL judge (§Optional): only for cosmetic-only diffs where both executed but the
            // fingerprints differ, and ONLY when explicitly enabled. When disabled the provider is
            // NEVER touched (no result data leaves the process — §1.11 / SC7).
            if (settings.EnableEvalJudge && !outcome.Passed && outcome.GoldExec.Success && outcome.GeneratedExec.Success)
            {
                judgeUsed = true;
                judgeVerdict = await RunJudgeAsync(evalCase.Question, outcome.GoldExec, outcome.GeneratedExec, settings, ct);
            }

            sw.Stop();
            return new McpEvalResult
            {
                EvalRunId = runId,
                EvalCaseId = evalCase.Id,
                GeneratedSql = outcome.GeneratedSql,
                Passed = outcome.Passed,
                FailureTag = failureTag,
                ExecutionError = executionError,
                JudgeUsed = judgeUsed,
                JudgeVerdict = judgeVerdict,
                ResultRowCount = outcome.GeneratedExec.Success ? outcome.GeneratedExec.Rows.Count : null,
                ExecutionTimeMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Eval case {CaseId} in run {RunId} threw during evaluation", evalCase.Id, runId);
            // A thrown exception means the harness could not evaluate this case (infra/generation), NOT that
            // the model produced wrong SQL — tag it so it is excluded from the accuracy denominator.
            return FailedResult(runId, evalCase.Id, null, McpEvalFailureTag.HarnessError, ex.Message, sw);
        }
    }

    public async Task<CaseEvaluation> EvaluateCasePassesAsync(
        int dataSourceId,
        string question,
        string goldSql,
        string? goldResultFingerprint,
        string? extraContext,
        CancellationToken ct)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found.");

        // Reuses the exact read-only generate+execute+compare core (SqlReadOnlyAstValidator + guardrail
        // via ExecuteReadOnlyAsync) — no judge, no persistence. Safe for the replay gate (§1.5). Generation
        // is pinned to temperature 0 so a baseline↔candidate flip reflects the injected lesson, not sampling
        // noise (both the baseline and candidate replay generations run through here).
        var outcome = await GenerateExecuteCompareAsync(
            dataSource, question, goldSql, goldResultFingerprint, extraContext, settings,
            generationTemperature: 0.0m, ct);

        // Measurable ONLY when both sides actually executed — a guardrail/AST rejection or a provider/
        // connection error makes the pass/fail meaningless, so the replay gate must exclude it rather than
        // treat it as a clean fail (false flip) or a clean pass turning into a fail (false regression).
        var measurable = outcome.GoldExec.Success && outcome.GeneratedExec.Success;
        return new CaseEvaluation(outcome.Passed, measurable);
    }

    /// <summary>
    /// The reusable read-only core shared by the normal run and the replay-verification gate: fetch the
    /// smart context, append the optional <paramref name="extraContext"/> (a candidate learned-pattern block)
    /// to it, generate SQL, execute BOTH the gold and generated SQL strictly read-only via
    /// <see cref="ExecuteReadOnlyAsync"/> (AST + guardrail, ReadOnly forced true), and compare result-set
    /// fingerprints (with the frozen-gold-fingerprint fallback). Performs NO judge and NO persistence.
    /// </summary>
    private async Task<CaseOutcome> GenerateExecuteCompareAsync(
        DataSource dataSource,
        string question,
        string goldSql,
        string? goldResultFingerprint,
        string? extraContext,
        McpSettingsData settings,
        decimal? generationTemperature,
        CancellationToken ct)
    {
        var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(dataSource.Id, question, ct);
        var dialect = smartContext.DatabaseDialect ?? dataSource.DatabaseEngineType?.ToString();

        // The candidate lesson is injected as a suffix on the smart-context string only — generation is the
        // only thing that changes between the baseline and candidate replay passes.
        var fullContext = string.IsNullOrEmpty(extraContext)
            ? smartContext.FullContext
            : smartContext.FullContext + extraContext;

        var generated = await sqlGenerationService.GenerateAsync(llmProvider, fullContext, question, settings, ct, generationTemperature);

        var goldExec = await ExecuteReadOnlyAsync(dataSource, goldSql, dialect, settings, ct);
        var generatedExec = await ExecuteReadOnlyAsync(dataSource, generated.Sql, dialect, settings, ct);

        // Gold fingerprint prefers a fresh execution; falls back to the frozen fingerprint captured
        // at promotion time when the gold SQL can no longer execute.
        var goldFingerprint = goldExec.Success
            ? ResultSetFingerprint.Compute(goldExec.Rows)
            : goldResultFingerprint;
        var generatedFingerprint = generatedExec.Success
            ? ResultSetFingerprint.Compute(generatedExec.Rows)
            : null;

        var passed = generatedExec.Success
            && goldFingerprint != null
            && generatedFingerprint == goldFingerprint;

        return new CaseOutcome(passed, generated.Sql, generated.TablesUsed, smartContext.RelevantTables, goldExec, generatedExec);
    }

    /// <summary>
    /// Executes SQL strictly read-only. Read-only is enforced HERE (not inherited): the regex guardrail
    /// runs first, then the AST validator (fail-closed on parse failure), then the row-limit is applied,
    /// and only then does the provider execute. Applies to BOTH the gold and the generated SQL.
    /// </summary>
    private async Task<ProviderQueryResult> ExecuteReadOnlyAsync(
        DataSource dataSource,
        string sql,
        string? dialect,
        McpSettingsData settings,
        CancellationToken ct)
    {
        // ReadOnly is forced true regardless of the per-project EnforceReadOnly flag — the eval harness
        // must never mutate a live data source.
        var guardrail = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
        {
            ReadOnly = true,
            DetectPii = settings.EnablePiiDetection,
            CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
        });

        if (!guardrail.IsValid)
        {
            return new ProviderQueryResult { Success = false, ErrorMessage = guardrail.Error };
        }

        var astError = readOnlyAstValidator.Validate(sql, dialect);
        if (astError != null)
        {
            return new ProviderQueryResult { Success = false, ErrorMessage = astError };
        }

        var limitedSql = guardrailService.ApplyRowLimit(sql, settings.MaxRowLimit, dataSource.DatabaseEngineType?.ToString());
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExecutionTimeout);

        return await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);
    }

    private async Task<string?> RunJudgeAsync(
        string question,
        ProviderQueryResult goldExec,
        ProviderQueryResult generatedExec,
        McpSettingsData settings,
        CancellationToken ct)
    {
        var customPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;
        var goldText = RedactAndFormat(goldExec.Rows, customPatterns);
        var generatedText = RedactAndFormat(generatedExec.Rows, customPatterns);

        var systemPrompt = """
            You are a strict evaluator for a text-to-SQL system. You are given a question and two result
            sets (the GOLD/expected result and the GENERATED result). Sensitive values have been redacted.
            Decide whether the two result sets are semantically EQUIVALENT (they answer the question with
            the same data, ignoring column ordering, formatting, or cosmetic differences).
            Answer with exactly one word: EQUIVALENT or DIFFERENT, optionally followed by a short reason.
            """;

        var userMessage = new StringBuilder()
            .Append("QUESTION: ").AppendLine(question).AppendLine()
            .AppendLine("GOLD RESULT (redacted):").AppendLine(goldText).AppendLine()
            .AppendLine("GENERATED RESULT (redacted):").AppendLine(generatedText)
            .ToString();

        var request = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userMessage)],
            Temperature = 0.0m,
            MaxTokens = 256
        };

        var response = await llmProvider.CompleteAsync(request, ct);
        return response.Content;
    }

    // Redacts PII columns via the existing guardrail detection, then renders a compact, capped textual
    // sample. Only the redacted, structural representation ever reaches the LLM (§1.6 / §1.11).
    private string RedactAndFormat(List<Dictionary<string, object?>> rows, List<string>? customPatterns)
    {
        if (rows.Count == 0)
        {
            return "(no rows)";
        }

        var columns = rows[0].Keys.ToList();
        var piiColumns = columns
            .Where(x => guardrailService.IsPiiColumn(x, customPatterns))
            .ToList();

        var builder = new StringBuilder();
        builder.Append("columns: ").AppendLine(string.Join(", ", columns));

        var shown = 0;
        foreach (var row in rows)
        {
            if (shown >= MaxJudgeRows)
            {
                builder.Append("... (").Append(rows.Count - MaxJudgeRows).AppendLine(" more rows)");
                break;
            }

            var safeRow = piiColumns.Count > 0 ? guardrailService.MaskPiiValues(row, piiColumns) : row;
            var cells = columns.Select(x => $"{x}={FormatCell(safeRow.GetValueOrDefault(x))}");
            builder.AppendLine(string.Join(" | ", cells));
            shown++;
        }

        return builder.ToString();
    }

    private static string FormatCell(object? value)
    {
        return value switch
        {
            null => "NULL",
            DBNull => "NULL",
            _ => value.ToString() ?? "NULL"
        };
    }

    private static McpEvalFailureTag DetermineFailureTag(
        bool passed,
        IReadOnlyList<string> generatedTables,
        IReadOnlyList<string> relevantTables,
        ProviderQueryResult goldExec,
        ProviderQueryResult generatedExec)
    {
        if (passed)
        {
            return McpEvalFailureTag.None;
        }

        if (ReferencesUnknownTable(generatedTables, relevantTables))
        {
            return McpEvalFailureTag.RetrievalFailure;
        }

        if (!generatedExec.Success || !goldExec.Success)
        {
            return McpEvalFailureTag.ExecutionError;
        }

        return McpEvalFailureTag.SqlReasoningFailure;
    }

    // A table the generated SQL touches that the smart-context retrieval never surfaced signals a
    // RETRIEVAL failure rather than a reasoning failure. Only meaningful when smart retrieval actually
    // populated RelevantTables — otherwise we cannot distinguish and fall through to reasoning.
    private static bool ReferencesUnknownTable(IReadOnlyList<string> generatedTables, IReadOnlyList<string> relevantTables)
    {
        if (relevantTables.Count == 0 || generatedTables.Count == 0)
        {
            return false;
        }

        var known = relevantTables
            .Select(LastSegment)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return generatedTables
            .Select(LastSegment)
            .Any(x => !known.Contains(x));
    }

    private static string LastSegment(string qualifiedName)
    {
        var idx = qualifiedName.LastIndexOf('.');
        return idx >= 0 ? qualifiedName[(idx + 1)..] : qualifiedName;
    }

    private static McpEvalResult FailedResult(
        int runId, int caseId, string? generatedSql, McpEvalFailureTag tag, string error, Stopwatch sw)
    {
        sw.Stop();
        return new McpEvalResult
        {
            EvalRunId = runId,
            EvalCaseId = caseId,
            GeneratedSql = generatedSql,
            Passed = false,
            FailureTag = tag,
            ExecutionError = error,
            ExecutionTimeMs = (int)sw.ElapsedMilliseconds
        };
    }

    private sealed class EvalCaseData
    {
        public int Id { get; init; }
        public int DataSourceId { get; init; }
        public string Question { get; init; } = null!;
        public string GoldSql { get; init; } = null!;
        public string? GoldResultFingerprint { get; init; }
    }

    // The result of one read-only generate+execute+compare pass — everything the run needs to build an
    // McpEvalResult (failure tag, judge, row count) and everything the replay gate needs (Passed only).
    private sealed record CaseOutcome(
        bool Passed,
        string? GeneratedSql,
        IReadOnlyList<string> GeneratedTables,
        IReadOnlyList<string> RelevantTables,
        ProviderQueryResult GoldExec,
        ProviderQueryResult GeneratedExec);
}
