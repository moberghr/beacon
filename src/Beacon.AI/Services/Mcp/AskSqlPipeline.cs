using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Eval;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.Mcp;

/// <summary>
/// The shared generate → validate → repair → execute core, moved here verbatim from
/// <c>ProjectAskTool.GenerateAndExecuteSqlAsync</c> (spec item ①). Control flow, rendered messages and the
/// shared repair budget of 2 are unchanged; the only substitutions are structural: signal-builder calls
/// became <see cref="AskSqlOutcome"/> fields, query-execution calls became <see cref="IAskSqlExecutor"/>
/// calls, and every guardrail / AST / schema / lint check goes through the one shared
/// <see cref="ISqlExecutionGate"/> (spec <c>sql-execution-gate</c>) so a later gate is added once for
/// every path. Adoption rules per repair point are preserved exactly: schema and lint repairs require
/// the retry to clear the schema gate too; dry-run, execution-error and empty-result repairs require
/// only the read-only gate, as before.
/// </summary>
internal sealed class AskSqlPipeline(
    IKnowledgeGraphService knowledgeGraph,
    ISqlGenerationService sqlGenerationService,
    ISqlExecutionGate gate,
    ILogger<AskSqlPipeline> logger) : IAskSqlPipeline
{
    // Non-zero so the N samples diverge; provider-agnostic (§9.4) — no per-provider tuning.
    private const decimal SelfConsistencyTemperature = 0.7m;

    public async Task<AskSqlOutcome> RunAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        int projectId,
        string question,
        McpSettingsData settings,
        IAskSqlExecutor executor,
        AskSqlPipelineOptions options,
        CancellationToken ct)
    {
        var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(dataSourceId, projectId, question, ct);

        // The replay gate injects a candidate learned-pattern block as a suffix on the context string only.
        var fullContext = string.IsNullOrEmpty(options.ExtraContext)
            ? smartContext.FullContext
            : smartContext.FullContext + options.ExtraContext;

        var execute = options.Execute;
        var repairs = new List<AskRepairStep>();

        // Built once and reused by every gate evaluation below (NF1) so the SQL that ultimately runs — after
        // a dry-run, execution or empty-result repair swaps it out — is re-linted against the same join /
        // PK / catalog context rather than describing stale, already-superseded SQL.
        var lintContext = new SchemaLintContext(
            smartContext.JoinPaths.SelectMany(x => x.Steps).ToList(),
            smartContext.PrimaryKeyCatalog,
            smartContext.SchemaCatalog);

        // Full gate: read-only + schema catalog + semantic lint. Used for the selected candidate and for the
        // schema / lint repairs, whose adoption depends on the schema verdict.
        SqlGateReport EvaluateFull(string sql)
        {
            return gate.Evaluate(SqlGateRequest.FromSettings(sql, smartContext.DatabaseDialect, settings) with
            {
                Catalog = smartContext.SchemaCatalog,
                LintContext = lintContext
            });
        }

        // The single low-temperature candidate is ALWAYS generated first (spec §⑥b): its distinct
        // table count decides whether self-consistency voting runs at all (R7), and it is itself one
        // of the candidates the vote considers, so a voted winner never loses the single candidate's
        // chance to be elected.
        var singleCandidate = await sqlGenerationService.GenerateAsync(
            llmProvider, fullContext, question, settings, ct, options.GenerationTemperature);
        var tableCount = singleCandidate.TablesUsed
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Self-consistency voting (spec §⑥b). Runs ONLY when execution is requested, the setting and
        // per-call option both allow it, and the single candidate touches at least
        // SelfConsistencyMinTables tables (R7 — a single-table question never requests extra
        // candidates). The elected winner's SqlGenerationResult — SQL, tables, assumptions and
        // clarification hint alike (F001) — replaces the single candidate and is fed into the
        // EXISTING repair loop below unchanged. If voting is disabled, gated off, or produces no
        // majority (no candidate validated+executed), the single candidate proceeds unchanged.
        string? votingNote = null;
        SqlGenerationResult? votedWinner = null;

        if (execute && settings.EnableSelfConsistency && options.AllowSelfConsistency && tableCount >= settings.SelfConsistencyMinTables)
        {
            try
            {
                var vote = await RunSelfConsistencyVoteAsync(
                    llmProvider, dataSourceId, question, settings, smartContext, fullContext, executor, singleCandidate, ct);
                votedWinner = vote.Winner;
                votingNote = vote.Note;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail-closed (2026-07-13 lesson): a broken voting path must never fail the ask; it falls
                // back to the already-validated single candidate. §1.11 — no SQL, no question text, no
                // provider error detail, just enough to diagnose.
                logger.LogWarning(
                    "Self-consistency voting failed for data source {DataSourceId} ({ExceptionType}); using single-candidate generation.",
                    dataSourceId, ex.GetType().Name);
            }
        }

        // A voted winner had its result set agreed on by the majority of independent candidates — including
        // agreeing that zero rows is the answer. Suppress the empty-result repair below for that winner so a
        // single loosened retry can't override the consensus with spurious rows.
        var electedByVote = votedWinner != null;
        var selectedCandidate = votedWinner ?? singleCandidate;

        var generatedSql = selectedCandidate.Sql;
        var tables = (IReadOnlyList<string>)selectedCandidate.TablesUsed;
        var assumptions = selectedCandidate.Assumptions ?? [];
        var clarificationHint = selectedCandidate.ClarificationHint;

        var initialSql = generatedSql;
        string? correctedSql = null;
        IReadOnlyList<string> columnsUsed = [];

        var text = $"### Generated SQL\n```sql\n{generatedSql}\n```\n\n";
        if (assumptions.Count > 0)
        {
            text += "### Assumptions\n";
            foreach (var assumption in assumptions)
            {
                text += $"- {assumption}\n";
            }

            text += "\n";
        }

        if (clarificationHint != null)
        {
            text += $"**Clarification suggested:** {clarificationHint}\n\n";
        }

        if (votingNote != null)
        {
            text += votingNote;
        }

        var initialReport = EvaluateFull(generatedSql);
        if (initialReport.Blocked)
        {
            var validationError = initialReport.BlockReason;
            text += $"**Validation Error:** {validationError}\n";
            return new AskSqlOutcome(initialSql, generatedSql, tables, false, text, null,
                validationError, null, null, repairs, false, votingNote, assumptions, clarificationHint, [],
                null, []);
        }

        // Explicit budget shared by ALL repair triggers (schema, dry-run, execution error, empty result)
        var repairAttempts = 0;
        const int maxRepairAttempts = 2;
        var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
        string? schemaValidationError = null;
        string? dryRunError = null;
        var emptyResultRetried = false;

        // Pre-execution schema validation — the verdict comes from the same gate evaluation that cleared
        // read-only above. A Skipped verdict (no catalog yet) is not a failure, matching the validator's
        // previous fail-open behaviour on an empty catalog.
        var currentReport = initialReport;
        columnsUsed = currentReport.ColumnsUsed;
        var schemaVerdict = currentReport.Verdicts.Schema;
        if (schemaVerdict.Status == SqlGateStatus.Fail && repairAttempts < maxRepairAttempts)
        {
            repairAttempts++;
            schemaValidationError = schemaVerdict.Message!;
            logger.LogInformation("Schema pre-validation failed, retrying. Error: {Error}", schemaVerdict.Message);

            var preValidationRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, schemaVerdict.Message!,
                fullContext, null, question, ct);

            var schemaRepairSql = (string?)null;
            var schemaRepairOk = false;
            if (preValidationRetry != null)
            {
                var retryReport = EvaluateFull(preValidationRetry);
                if (!retryReport.Blocked)
                {
                    schemaRepairSql = preValidationRetry;
                    schemaRepairOk = retryReport.Verdicts.Schema.Status != SqlGateStatus.Fail;
                    if (schemaRepairOk)
                    {
                        currentReport = retryReport;
                        columnsUsed = retryReport.ColumnsUsed;
                        text += $"*Initial query had schema errors ({schemaVerdict.Message}), retried.*\n\n";
                        text += $"### Corrected SQL\n```sql\n{preValidationRetry}\n```\n\n";
                        generatedSql = preValidationRetry;
                        correctedSql = preValidationRetry;
                    }
                }
            }

            repairs.Add(new AskRepairStep("schema", schemaVerdict.Message!, schemaRepairSql, schemaRepairOk));
        }

        IReadOnlyList<SqlLintFinding> RelintFinalSql(string finalSql)
        {
            return settings.EnableSemanticLint
                ? EvaluateFull(finalSql).LintFindings
                : [];
        }

        // Semantic lint (spec item ⑥a): deterministic checks the schema-existence validator above
        // cannot catch — an undeclared join, an aggregate fanning out over a one-to-many join, or a
        // non-aggregated SELECT column missing from GROUP BY. Runs after schema validation (and its
        // repair) so it never lints SQL that would already be rejected on schema grounds, and spends
        // at most one repair attempt out of the SAME shared budget (R6). Gated by EnableSemanticLint (R9).
        IReadOnlyList<SqlLintFinding> lintFindings = [];
        if (settings.EnableSemanticLint)
        {
            // currentReport already describes generatedSql (the initial candidate or the adopted schema repair).
            var initialLintFindings = currentReport.LintFindings;
            lintFindings = initialLintFindings;

            if (initialLintFindings.Count > 0 && repairAttempts < maxRepairAttempts)
            {
                repairAttempts++;
                var lintError = "Semantic check: " + string.Join("; ", initialLintFindings.Select(x => $"{x.Code}: {x.Message}"));
                logger.LogInformation("Semantic lint found {Count} finding(s), retrying.", initialLintFindings.Count);

                var lintRetry = await sqlGenerationService.RetryWithErrorAsync(
                    llmProvider, systemPrompt, generatedSql, lintError,
                    fullContext, null, question, ct);

                var lintRepairSql = (string?)null;
                var lintRepairOk = false;
                if (lintRetry != null)
                {
                    var retryReport = EvaluateFull(lintRetry);
                    if (!retryReport.Blocked && retryReport.Verdicts.Schema.Status != SqlGateStatus.Fail)
                    {
                        var retryLintFindings = retryReport.LintFindings;
                        if (retryLintFindings.Count < initialLintFindings.Count)
                        {
                            lintRepairSql = lintRetry;
                            lintRepairOk = true;
                            text += $"*Initial query had semantic warnings ({lintError}), retried.*\n\n";
                            text += $"### Corrected SQL\n```sql\n{lintRetry}\n```\n\n";
                            generatedSql = lintRetry;
                            correctedSql = lintRetry;
                            lintFindings = retryLintFindings;
                        }
                    }
                }

                repairs.Add(new AskRepairStep("lint", lintError, lintRepairSql, lintRepairOk));
            }
        }

        // Rendered after the results block below (or right here on the !execute path) — bullets per
        // finding on whatever SQL proceeds, so a warning always travels with the SQL it describes.
        void AppendSemanticWarnings()
        {
            if (lintFindings.Count == 0)
            {
                return;
            }

            text += "\n### Semantic warnings\n";
            foreach (var finding in lintFindings)
            {
                text += $"- **{finding.Code}:** {finding.Message}\n";
            }
        }

        if (!execute)
        {
            lintFindings = RelintFinalSql(generatedSql);
            AppendSemanticWarnings();
            return new AskSqlOutcome(initialSql, generatedSql, tables, true, text, null,
                null, schemaValidationError, null, repairs, false, votingNote, assumptions, clarificationHint, lintFindings,
                correctedSql, columnsUsed);
        }

        // Dry-run through the provider (EXPLAIN / sp_describe_first_result_set) before real execution.
        // A dry-run failure only spends a repair attempt — it never hard-blocks execution.
        dryRunError = await TryDryRunAsync(executor, dataSourceId, generatedSql, ct);
        if (dryRunError != null && repairAttempts < maxRepairAttempts)
        {
            repairAttempts++;
            logger.LogInformation("Dry-run validation failed, retrying. Error: {Error}", dryRunError);

            var dryRunRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, dryRunError,
                fullContext, null, question, ct);

            var dryRunRepairSql = (string?)null;
            var dryRunRepairOk = false;
            if (dryRunRetry != null)
            {
                if (ValidateGeneratedSql(dryRunRetry, settings, smartContext.DatabaseDialect) == null)
                {
                    var retryDryRunError = await TryDryRunAsync(executor, dataSourceId, dryRunRetry, ct);
                    dryRunRepairSql = dryRunRetry;
                    dryRunRepairOk = retryDryRunError == null;
                    text += $"*Initial query failed dry-run validation ({dryRunError}), retried.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{dryRunRetry}\n```\n\n";
                    generatedSql = dryRunRetry;
                    correctedSql = dryRunRetry;
                }
            }

            repairs.Add(new AskRepairStep("dry-run", dryRunError, dryRunRepairSql, dryRunRepairOk));
        }

        var execResult = await executor.ExecuteAsync(dataSourceId, generatedSql, 100, ct);

        if (!execResult.IsSuccess && execResult.ErrorMessage != null && repairAttempts < maxRepairAttempts)
        {
            repairAttempts++;
            logger.LogInformation("SQL error detected, retrying. Error: {Error}", execResult.ErrorMessage);
            // AST-resolved tables (aliases resolved, CTEs excluded) — generatedSql already cleared the gate.
            var tableNames = gate.Evaluate(SqlGateRequest.FromSettings(generatedSql, smartContext.DatabaseDialect, settings)).TablesUsed.ToList();
            var tablesContext = tableNames.Count > 0
                ? await knowledgeGraph.GetTablesContextAsync(dataSourceId, tableNames, ct)
                : null;

            var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, execResult.ErrorMessage,
                fullContext, tablesContext, question, ct);

            if (retriedSql != null)
            {
                if (ValidateGeneratedSql(retriedSql, settings, smartContext.DatabaseDialect) == null)
                {
                    var retryExec = await executor.ExecuteAsync(dataSourceId, retriedSql, 100, ct);
                    repairs.Add(new AskRepairStep("execution", execResult.ErrorMessage, retriedSql, retryExec.IsSuccess));
                    text += $"*Initial query failed ({execResult.ErrorMessage}), retried with corrected SQL.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{retriedSql}\n```\n\n";
                    text += retryExec.FormattedResult ?? $"**Execution Error:** {retryExec.ErrorMessage}\n";
                    lintFindings = RelintFinalSql(retriedSql);
                    AppendSemanticWarnings();
                    return new AskSqlOutcome(initialSql, retriedSql, tables, retryExec.IsSuccess, text, retryExec,
                        null, schemaValidationError, dryRunError, repairs, false, votingNote, assumptions, clarificationHint, lintFindings,
                        retriedSql, columnsUsed);
                }
            }

            repairs.Add(new AskRepairStep("execution", execResult.ErrorMessage, null, false));
        }
        else if (execResult.IsSuccess && execResult.RowCount == 0 && repairAttempts < maxRepairAttempts && !electedByVote && !QuestionExpectsCountOrExistence(question))
        {
            // Empty-result repair: one bounded retry; identical SQL or a second empty result
            // means zero rows is accepted as the answer.
            repairAttempts++;
            emptyResultRetried = true;
            logger.LogInformation("Query returned zero rows, attempting one retry");

            const string emptyResultError = "The query executed successfully but returned zero rows. Re-check filter values against the Examples in the schema, join paths, and value casing. If zero rows is genuinely the correct answer, return the identical SQL.";

            var emptyRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, emptyResultError,
                fullContext, null, question, ct);

            if (emptyRetry != null && !SqlEquals(emptyRetry, generatedSql))
            {
                if (ValidateGeneratedSql(emptyRetry, settings, smartContext.DatabaseDialect) == null)
                {
                    var retryExec = await executor.ExecuteAsync(dataSourceId, emptyRetry, 100, ct);
                    if (retryExec.IsSuccess && retryExec.RowCount > 0)
                    {
                        repairs.Add(new AskRepairStep("empty-result", emptyResultError, emptyRetry, true));
                        text += "*Initial query returned zero rows, retried with corrected SQL.*\n\n";
                        text += $"### Corrected SQL\n```sql\n{emptyRetry}\n```\n\n";
                        text += retryExec.FormattedResult;
                        lintFindings = RelintFinalSql(emptyRetry);
                        AppendSemanticWarnings();
                        return new AskSqlOutcome(initialSql, emptyRetry, tables, true, text, retryExec,
                            null, schemaValidationError, dryRunError, repairs, true, votingNote, assumptions, clarificationHint, lintFindings,
                            emptyRetry, columnsUsed);
                    }

                    repairs.Add(new AskRepairStep("empty-result", emptyResultError, emptyRetry, false));
                }
                else
                {
                    repairs.Add(new AskRepairStep("empty-result", emptyResultError, null, false));
                }
            }
            else
            {
                repairs.Add(new AskRepairStep("empty-result", emptyResultError, null, false));
            }
        }

        text += execResult.FormattedResult ?? $"**Execution Error:** {execResult.ErrorMessage}\n";
        lintFindings = RelintFinalSql(generatedSql);
        AppendSemanticWarnings();

        return new AskSqlOutcome(initialSql, generatedSql, tables, execResult.IsSuccess, text, execResult,
            null, schemaValidationError, dryRunError, repairs, emptyResultRetried, votingNote, assumptions, clarificationHint, lintFindings,
            correctedSql, columnsUsed);
    }

    // Requests (SelfConsistencyCandidateCount clamped to [1,8]) - 1 extra candidates CONCURRENTLY
    // (§6.1 — real concurrency is bounded by LlmRequestQueue behind the injected provider), adds
    // `singleCandidate` as one more vote member, validates + executes each read-only, and elects the
    // majority result set. Returns the winning SqlGenerationResult (F001 — so its Assumptions and
    // ClarificationHint flow into the outcome, not just its SQL/tables) + an agreement note, or a null
    // winner when no candidate validated and executed — in which case the caller keeps the single
    // candidate unchanged.
    private async Task<(SqlGenerationResult? Winner, string Note)> RunSelfConsistencyVoteAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        string question,
        McpSettingsData settings,
        SmartSchemaContext smartContext,
        string fullContext,
        IAskSqlExecutor executor,
        SqlGenerationResult singleCandidate,
        CancellationToken ct)
    {
        var totalCandidateCount = Math.Clamp(settings.SelfConsistencyCandidateCount, 1, 8);
        var extraCandidateCount = totalCandidateCount - 1;

        var extraCandidates = extraCandidateCount > 0
            ? await sqlGenerationService.GenerateCandidatesAsync(
                llmProvider, fullContext, question, settings, extraCandidateCount, SelfConsistencyTemperature, ct)
            : [];

        var candidates = new List<SqlGenerationResult> { singleCandidate };
        candidates.AddRange(extraCandidates);

        var evaluated = new List<(SqlGenerationResult Candidate, string Fingerprint, bool Ok)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // SECURITY (§1.5, lesson 2026-07-03): EVERY candidate — including the single low-temperature
            // one — must clear the same guardrail + AST read-only gate the single-candidate path uses
            // BEFORE it can reach ExecuteAsync. A candidate that fails validation is dropped and never executed.
            if (ValidateGeneratedSql(candidate.Sql, settings, smartContext.DatabaseDialect) != null)
            {
                continue;
            }

            var execResult = await executor.ExecuteAsync(dataSourceId, candidate.Sql, 100, ct);
            evaluated.Add((candidate, ResultFingerprint(execResult), execResult.IsSuccess));
        }

        // Lint each successfully-executed candidate once (cheap, pure) so a result-set tie breaks
        // toward fewer semantic-lint findings rather than pure first-seen order. Gated by
        // EnableSemanticLint like the repair-loop lint below (R9 — no lint opinion when disabled).
        var lintCountBySql = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in evaluated.Where(x => x.Ok))
        {
            lintCountBySql.TryAdd(candidate.Candidate.Sql, LintCountFor(candidate.Candidate.Sql, settings, smartContext));
        }

        var winnerSql = SelectMajority(
            evaluated.Select(x => (x.Candidate.Sql, x.Fingerprint, x.Ok)).ToList(),
            sql => lintCountBySql.GetValueOrDefault(sql, 0));

        if (winnerSql == null)
        {
            // The most diagnostically interesting outcome (model couldn't agree, or every candidate failed
            // validation/execution) must NOT be silent — otherwise voting can be effectively broken while
            // paying N× cost with an identical-looking response.
            logger.LogWarning(
                "Self-consistency voting found no agreeing executable candidate for data source {DataSourceId} ({Generated} generated, {Executed} executed); keeping the single candidate.",
                dataSourceId, candidates.Count, evaluated.Count(x => x.Ok));
            return (null, "*Self-consistency: no candidate produced an executable, agreed result; used single-candidate generation.*\n\n");
        }

        var winnerFingerprint = evaluated
            .First(x => x.Candidate.Sql == winnerSql)
            .Fingerprint;
        var agreementCount = evaluated.Count(x => x.Ok && x.Fingerprint == winnerFingerprint);
        var executedCount = evaluated.Count(x => x.Ok);
        var winner = candidates.First(x => x.Sql == winnerSql);

        logger.LogInformation(
            "Self-consistency voting: {Agree}/{Executed} candidates agreed (of {Generated} generated)",
            agreementCount, executedCount, candidates.Count);

        var note = $"*Self-consistency: {agreementCount} of {executedCount} executed candidate(s) agreed on this result set ({candidates.Count} generated).*\n\n";
        return (winner, note);
    }

    // Lints one candidate's SQL to feed SelectMajority's tie-break; returns 0 (no opinion) while
    // EnableSemanticLint is off, mirroring the repair-loop lint gate (R9).
    private int LintCountFor(string sql, McpSettingsData settings, SmartSchemaContext smartContext)
    {
        if (!settings.EnableSemanticLint)
        {
            return 0;
        }

        var lintContext = new SchemaLintContext(
            smartContext.JoinPaths.SelectMany(x => x.Steps).ToList(),
            smartContext.PrimaryKeyCatalog,
            smartContext.SchemaCatalog);

        return gate.Evaluate(SqlGateRequest.FromSettings(sql, smartContext.DatabaseDialect, settings) with
        {
            LintContext = lintContext
        }).LintFindings.Count;
    }

    // Result-set majority vote. Groups the SUCCESSFULLY-executed candidates by fingerprint and
    // returns the SQL of the largest group; ties break to fewer lint findings on the group's
    // (first-seen) representative SQL, then to the first-seen group. Returns null when no candidate
    // executed successfully (caller then keeps the single-candidate path). Pure + static so it is
    // unit-testable without an LLM or a database; `lintCountBySql` defaults to "no opinion" so callers
    // that never lint (or existing tests) can omit it entirely.
    internal static string? SelectMajority(
        IReadOnlyList<(string Sql, string Fingerprint, bool Ok)> candidates,
        Func<string, int>? lintCountBySql = null)
    {
        var successful = candidates
            .Where(x => x.Ok)
            .ToList();

        if (successful.Count == 0)
        {
            return null;
        }

        var lintCount = lintCountBySql ?? (_ => 0);

        return successful
            .GroupBy(x => x.Fingerprint)
            .Select(x => new { Group = x, Representative = x.First() })
            .OrderByDescending(x => x.Group.Count())
            .ThenBy(x => lintCount(x.Representative.Sql))
            .ThenBy(x => successful.FindIndex(y => y.Fingerprint == x.Group.Key))
            .First()
            .Representative
            .Sql;
    }

    // Order-independent result-set fingerprint for self-consistency voting: two candidates returning the
    // same rows in a different order (no stable ORDER BY) must produce the same fingerprint so they count as
    // agreeing. The MCP executor exposes the formatted markdown table, so canonicalize by trimming and
    // ordinally sorting its non-empty lines — the header/separator lines are identical across same-shaped
    // results, so the sort is stable and only row order is neutralized. The eval executor formats nothing
    // and exposes raw rows instead, so fall back to the row-multiset fingerprint there — otherwise every
    // executed candidate with the same row count would "agree" regardless of content. Internal for unit tests.
    internal static string ResultFingerprint(AskExecutionResult result)
    {
        if (result.FormattedResult == null && result.Rows != null)
        {
            return $"{result.IsSuccess}|{result.RowCount}|{ResultSetFingerprint.Compute(result.Rows)}";
        }

        var canonical = string.Join(
            "\n",
            (result.FormattedResult ?? "")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.Ordinal));

        return $"{result.IsSuccess}|{result.RowCount}|{canonical}";
    }

    // For existence/count-style questions zero rows is usually the correct answer — retrying
    // would burn a repair attempt second-guessing a legitimate result.
    internal static bool QuestionExpectsCountOrExistence(string question)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            question,
            @"\b(how many|count|number of|total of|are there|is there|any\b|exists?|do we have|does .{1,40} have|has .{1,40} ever)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private async Task<string?> TryDryRunAsync(IAskSqlExecutor executor, int dataSourceId, string sql, CancellationToken ct)
    {
        try
        {
            return await executor.DryRunAsync(dataSourceId, sql, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Dry-run infrastructure failures (connectivity, unsupported engine) never block the ask flow
            logger.LogWarning(ex, "Dry-run validation unavailable for data source {DataSourceId}", dataSourceId);
            return null;
        }
    }

    // Read-only gate only (regex guardrail + AST validator, §1.5) — the check every candidate and every
    // dry-run / execution-error / empty-result repair must clear before it may execute. Returns the block
    // reason, or null when the SQL may proceed.
    private string? ValidateGeneratedSql(string sql, McpSettingsData settings, string? dialect)
    {
        var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings));

        return report.Blocked ? report.BlockReason : null;
    }

    private static bool SqlEquals(string left, string right)
    {
        return string.Equals(
            left.Trim().TrimEnd(';'),
            right.Trim().TrimEnd(';'),
            StringComparison.OrdinalIgnoreCase);
    }
}
