using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectAskTool(
    IKnowledgeGraphService knowledgeGraph,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider,
    IServiceProvider serviceProvider,
    IProjectContext projectContext,
    McpAuditService auditService,
    McpSignalService signalService,
    SqlSchemaValidator schemaValidator,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    IQueryExecutionService queryExecutionService,
    IIntentClassifier intentClassifier,
    IDataSourceRouter dataSourceRouter,
    ISqlGenerationService sqlGenerationService,
    IKnowledgeAnswerService knowledgeAnswerService,
    ICrossSourceQueryService crossSourceQueryService,
    ILogger<ProjectAskTool> logger)
{
    [McpServerTool(Name = "ask", Title = "Ask a Data Question", ReadOnly = true, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Ask a natural language question about your data or project. For data queries, Beacon auto-detects the right data source(s), generates SQL, and executes it. For conceptual questions (e.g., 'how do notifications work?'), it answers from project documentation and knowledge base.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("Your question in natural language (e.g., 'How many orders were placed last week?')")]
        string question,
        [Description("Optional. Specify project if your API key has access to multiple projects.")]
        int? project_id = null,
        [Description("Whether to execute the generated SQL (default: true)")]
        bool execute = true,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var signal = new McpSignalBuilder()
            .SetTool("ask")
            .SetQuestion(question ?? "")
            .SetUserId(projectContext.UserId);

        if (string.IsNullOrEmpty(question))
            return await FailAsync(signal, sw, null, question ?? "", "Missing required parameter: question", cancellationToken);

        var resolveError = ToolHelper.ResolveProjectId(projectContext, project_id, out var projectId);
        if (resolveError != null)
            return await FailAsync(signal, sw, null, question, resolveError, cancellationToken);

        signal.SetProjectId(projectId);

        try
        {
            var llmProvider = serviceProvider.GetService(typeof(ILlmProvider)) as ILlmProvider;
            if (llmProvider == null)
            {
                return await FailAsync(signal, sw, projectId, question,
                    "AI features not configured. Add LLM configuration to use the 'ask' tool.", CancellationToken.None);
            }

            var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

            // Classify intent — data query vs knowledge question
            var intent = await intentClassifier.ClassifyAsync(llmProvider, question, cancellationToken);
            signal.SetIntent(intent == IntentClassification.Knowledge ? "Knowledge" : "DataQuery");

            if (intent == IntentClassification.Knowledge)
            {
                var knowledgeResult = await knowledgeAnswerService.AnswerAsync(llmProvider, projectId, question, settings, cancellationToken);
                sw.Stop();
                signal.SetResult(null, (int)sw.ElapsedMilliseconds, true);
                await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                    question, null, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
                await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
                return ToolHelper.Success(knowledgeResult);
            }

            var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, cancellationToken);

            if (dataSources.Count == 0)
            {
                return await FailAsync(signal, sw, projectId, question,
                    "This project has no data sources configured.", cancellationToken);
            }

            // Route to the right data source(s)
            var routing = await dataSourceRouter.RouteAsync(llmProvider, dataSources, question, cancellationToken);
            signal.SetRouting(routing.Sources.Select(x => (x.DataSourceId, x.DataSourceName, x.Reason)).ToList());

            var text = $"# Question: {question}\n\n";

            if (routing.Sources.Count == 0)
            {
                return await FailAsync(signal, sw, projectId, question,
                    "Could not determine which data source to query for this question.", cancellationToken);
            }

            var askSucceeded = true;
            JsonNode? resultPayload = null;
            string? generatedSql = null;
            string? correctedSql = null;

            // Generate and execute SQL
            if (routing.Sources.Count == 1)
            {
                var source = routing.Sources[0];
                text += $"## Data Source: {source.DataSourceName}\n";
                text += $"**Reasoning:** {source.Reason}\n\n";

                var outcome = await GenerateAndExecuteSqlAsync(
                    llmProvider, source.DataSourceId, projectId, question, settings, execute, signal, cancellationToken);
                text += outcome.Text;
                askSucceeded = outcome.Succeeded;
                resultPayload = outcome.ResultPayload;
                generatedSql = outcome.GeneratedSql;
                correctedSql = outcome.CorrectedSql;
            }
            else
            {
                text += "## Cross-Source Query\n\n";
                foreach (var source in routing.Sources)
                    text += $"- **{source.DataSourceName}** (ID: {source.DataSourceId}): {source.Reason}\n";
                text += "\n";

                var (crossText, crossSucceeded) = await crossSourceQueryService.ExecuteAsync(
                    llmProvider, routing.Sources, projectId, question, settings, execute, signal, cancellationToken);
                text += crossText;
                askSucceeded = crossSucceeded;
                // Cross-source flow builds its markdown internally; structured content for this path
                // carries signal_id + generated_sql (from the signal builder) only — no columns/rows payload.
                generatedSql = signal.GeneratedSql;
            }

            sw.Stop();
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, askSucceeded);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                question, null, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            var signalId = await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
            if (signalId is { } id)
            {
                text += $"\n\n_signal_id: {id}_";
            }
            return ToolHelper.Success(text, BuildAskStructuredContent(signalId, generatedSql, correctedSql, resultPayload));
        }
        catch (Exception ex)
        {
            sw.Stop();
            signal.SetExecutionFailed(ex.Message);
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                question, null, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            await signalService.RecordSignalAsync(signal.Build(), CancellationToken.None);
            // §1.11 — ex.Message can quote user input; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "ask", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "ask"));
        }
    }

    // Internal for repair-flow tests (InternalsVisibleTo Beacon.Tests). projectId is the caller's
    // AUTHORIZED project — project-scoped grounding (glossary, golden cases) keys on it, never on the
    // data source's own project links (codex PR-11 R4 fix).
    internal async Task<AskSqlOutcome> GenerateAndExecuteSqlAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        int projectId,
        string question,
        Core.Models.McpSettingsData settings,
        bool execute,
        McpSignalBuilder signal,
        CancellationToken ct)
    {
        var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(dataSourceId, projectId, question, ct);
        signal.SetDataSourceId(dataSourceId);

        // Self-consistency voting pre-stage (spec §⑤). Runs ONLY when enabled and execution is
        // requested — it must execute each candidate read-only to compare result sets. The elected
        // winner becomes `generatedSql` and is fed into the EXISTING repair loop below unchanged.
        // If voting is disabled, or produces no majority (no candidate validated+executed), we fall
        // through to the single-candidate path below, behaving exactly as before.
        string? generatedSql = null;
        List<string>? tablesUsed = null;
        string? votingNote = null;

        if (settings.EnableSelfConsistency && execute)
        {
            var vote = await RunSelfConsistencyVoteAsync(
                llmProvider, dataSourceId, question, settings, smartContext, ct);
            generatedSql = vote.Sql;
            tablesUsed = vote.Tables;
            votingNote = vote.Note;
        }

        // A voted winner had its result set agreed on by the majority of independent candidates — including
        // agreeing that zero rows is the answer. Suppress the empty-result repair below for that winner so a
        // single loosened retry can't override the consensus with spurious rows.
        var electedByVote = generatedSql != null;

        if (generatedSql == null)
        {
            var sqlResult = await sqlGenerationService.GenerateAsync(llmProvider, smartContext.FullContext, question, settings, ct);
            generatedSql = sqlResult.Sql;
            tablesUsed = sqlResult.TablesUsed;
        }

        signal.SetGeneratedSql(generatedSql, tablesUsed);

        // The SQL shown under "### Generated SQL" — kept stable for the structured payload even
        // when a repair below reassigns generatedSql to a corrected candidate.
        var initialSql = generatedSql;
        string? correctedSql = null;

        var text = $"### Generated SQL\n```sql\n{generatedSql}\n```\n\n";
        if (votingNote != null)
        {
            text += votingNote;
        }

        var validationError = ValidateGeneratedSql(generatedSql, settings, smartContext.DatabaseDialect);
        if (validationError != null)
        {
            signal.SetExecutionFailed(validationError);
            text += $"**Validation Error:** {validationError}\n";
            return new AskSqlOutcome(text, false, null, initialSql, null);
        }

        // Explicit budget shared by ALL repair triggers (schema, dry-run, execution error, empty result)
        var repairAttempts = 0;
        const int maxRepairAttempts = 2;
        var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";

        // Pre-execution schema validation
        var schemaCheck = schemaValidator.Validate(generatedSql, smartContext.SchemaCatalog, smartContext.DatabaseDialect);
        signal.SetColumnsUsed(schemaCheck.ColumnsUsed);
        if (!schemaCheck.IsValid && repairAttempts < maxRepairAttempts)
        {
            repairAttempts++;
            signal.SetSchemaValidationFailed(schemaCheck.Error!);
            logger.LogInformation("Schema pre-validation failed, retrying. Error: {Error}", schemaCheck.Error);

            var preValidationRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, schemaCheck.Error!,
                smartContext.FullContext, null, question, ct);

            if (preValidationRetry != null)
            {
                if (ValidateGeneratedSql(preValidationRetry, settings, smartContext.DatabaseDialect) == null)
                {
                    var retrySchemaCheck = schemaValidator.Validate(preValidationRetry, smartContext.SchemaCatalog, smartContext.DatabaseDialect);
                    signal.SetRetry(preValidationRetry, retrySchemaCheck.IsValid);
                    if (retrySchemaCheck.IsValid)
                    {
                        signal.SetColumnsUsed(retrySchemaCheck.ColumnsUsed);
                        text += $"*Initial query had schema errors ({schemaCheck.Error}), retried.*\n\n";
                        text += $"### Corrected SQL\n```sql\n{preValidationRetry}\n```\n\n";
                        generatedSql = preValidationRetry;
                        correctedSql = preValidationRetry;
                    }
                }
            }
        }

        if (!execute)
        {
            return new AskSqlOutcome(text, true, null, initialSql, correctedSql);
        }

        // Dry-run through the provider (EXPLAIN / sp_describe_first_result_set) before real execution.
        // A dry-run failure only spends a repair attempt — it never hard-blocks execution.
        var dryRunError = await TryDryRunAsync(dataSourceId, generatedSql, ct);
        if (dryRunError != null && repairAttempts < maxRepairAttempts)
        {
            repairAttempts++;
            signal.SetDryRunFailed(dryRunError);
            logger.LogInformation("Dry-run validation failed, retrying. Error: {Error}", dryRunError);

            var dryRunRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, dryRunError,
                smartContext.FullContext, null, question, ct);

            if (dryRunRetry != null)
            {
                if (ValidateGeneratedSql(dryRunRetry, settings, smartContext.DatabaseDialect) == null)
                {
                    var retryDryRunError = await TryDryRunAsync(dataSourceId, dryRunRetry, ct);
                    signal.SetRetry(dryRunRetry, retryDryRunError == null);
                    text += $"*Initial query failed dry-run validation ({dryRunError}), retried.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{dryRunRetry}\n```\n\n";
                    generatedSql = dryRunRetry;
                    correctedSql = dryRunRetry;
                }
            }
        }

        var execResult = await queryExecutionService.ExecuteAsync(dataSourceId, generatedSql, 100, ct);

        if (!execResult.IsSuccess && execResult.ErrorMessage != null && repairAttempts < maxRepairAttempts)
        {
            repairAttempts++;
            signal.SetExecutionFailed(execResult.ErrorMessage);
            logger.LogInformation("SQL error detected, retrying. Error: {Error}", execResult.ErrorMessage);
            var tableNames = SqlParsingHelper.ExtractTableNamesFromSql(generatedSql);
            var tablesContext = tableNames.Count > 0
                ? await knowledgeGraph.GetTablesContextAsync(dataSourceId, tableNames, ct)
                : null;

            var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, execResult.ErrorMessage,
                smartContext.FullContext, tablesContext, question, ct);

            if (retriedSql != null)
            {
                if (ValidateGeneratedSql(retriedSql, settings, smartContext.DatabaseDialect) == null)
                {
                    var retryExec = await queryExecutionService.ExecuteAsync(dataSourceId, retriedSql, 100, ct);
                    signal.SetRetry(retriedSql, retryExec.IsSuccess);
                    text += $"*Initial query failed ({execResult.ErrorMessage}), retried with corrected SQL.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{retriedSql}\n```\n\n";
                    text += retryExec.FormattedResult ?? $"**Execution Error:** {retryExec.ErrorMessage}\n";
                    return new AskSqlOutcome(text, retryExec.IsSuccess, retryExec.Structured, initialSql, retriedSql);
                }
            }
        }
        else if (execResult.IsSuccess && execResult.RowCount == 0 && repairAttempts < maxRepairAttempts && !electedByVote && !QuestionExpectsCountOrExistence(question))
        {
            // Empty-result repair: one bounded retry; identical SQL or a second empty result
            // means zero rows is accepted as the answer.
            repairAttempts++;
            signal.SetEmptyResultRetry();
            logger.LogInformation("Query returned zero rows, attempting one retry");

            var emptyRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql,
                "The query executed successfully but returned zero rows. Re-check filter values against the Examples in the schema, join paths, and value casing. If zero rows is genuinely the correct answer, return the identical SQL.",
                smartContext.FullContext, null, question, ct);

            if (emptyRetry != null && !SqlEquals(emptyRetry, generatedSql))
            {
                if (ValidateGeneratedSql(emptyRetry, settings, smartContext.DatabaseDialect) == null)
                {
                    var retryExec = await queryExecutionService.ExecuteAsync(dataSourceId, emptyRetry, 100, ct);
                    if (retryExec.IsSuccess && retryExec.RowCount > 0)
                    {
                        signal.SetRetry(emptyRetry, true);
                        text += "*Initial query returned zero rows, retried with corrected SQL.*\n\n";
                        text += $"### Corrected SQL\n```sql\n{emptyRetry}\n```\n\n";
                        text += retryExec.FormattedResult;
                        return new AskSqlOutcome(text, true, retryExec.Structured, initialSql, emptyRetry);
                    }

                    signal.SetRetry(emptyRetry, false);
                }
            }
        }

        text += execResult.FormattedResult ?? $"**Execution Error:** {execResult.ErrorMessage}\n";

        return new AskSqlOutcome(text, execResult.IsSuccess, execResult.Structured, initialSql, correctedSql);
    }

    // Non-zero so the N samples diverge; provider-agnostic (§9.4) — no per-provider tuning.
    private const decimal SelfConsistencyTemperature = 0.7m;

    // Generates N candidates, validates + executes each read-only, and elects the majority
    // result set. Returns the winning SQL (+ its tables + an agreement note) or all-nulls when no
    // candidate validated and executed — in which case the caller falls back to single generation.
    private async Task<(string? Sql, List<string>? Tables, string? Note)> RunSelfConsistencyVoteAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        string question,
        Core.Models.McpSettingsData settings,
        Beacon.AI.Services.Knowledge.SmartSchemaContext smartContext,
        CancellationToken ct)
    {
        var candidateCount = Math.Clamp(settings.SelfConsistencyCandidateCount, 1, 8);

        var candidates = await sqlGenerationService.GenerateCandidatesAsync(
            llmProvider, smartContext.FullContext, question, settings, candidateCount, SelfConsistencyTemperature, ct);

        if (candidates.Count == 0)
        {
            logger.LogWarning(
                "Self-consistency voting produced no candidates for data source {DataSourceId}; falling back to single-candidate generation.",
                dataSourceId);
            return (null, null, "*Self-consistency: no candidates were generated; used single-candidate generation.*\n\n");
        }

        var evaluated = new List<(string Sql, string Fingerprint, bool Ok)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // SECURITY (§1.5, lesson 2026-07-03): EVERY candidate must clear the same guardrail +
            // AST read-only gate the single-candidate path uses BEFORE it can reach ExecuteAsync.
            // A candidate that fails validation is dropped and is never executed.
            if (ValidateGeneratedSql(candidate.Sql, settings, smartContext.DatabaseDialect) != null)
            {
                continue;
            }

            var execResult = await queryExecutionService.ExecuteAsync(dataSourceId, candidate.Sql, 100, ct);
            evaluated.Add((candidate.Sql, ResultFingerprint(execResult), execResult.IsSuccess));
        }

        var winner = SelectMajority(evaluated);
        if (winner == null)
        {
            // The most diagnostically interesting outcome (model couldn't agree, or every candidate failed
            // validation/execution) must NOT be silent — otherwise voting can be effectively broken while
            // paying N× cost with an identical-looking response.
            logger.LogWarning(
                "Self-consistency voting found no agreeing executable candidate for data source {DataSourceId} ({Generated} generated, {Executed} executed); falling back to single-candidate generation.",
                dataSourceId, candidates.Count, evaluated.Count(x => x.Ok));
            return (null, null, "*Self-consistency: no candidate produced an executable, agreed result; used single-candidate generation.*\n\n");
        }

        var winnerFingerprint = evaluated
            .First(x => x.Sql == winner)
            .Fingerprint;
        var agreementCount = evaluated.Count(x => x.Ok && x.Fingerprint == winnerFingerprint);
        var executedCount = evaluated.Count(x => x.Ok);
        var winnerTables = candidates
            .First(x => x.Sql == winner)
            .TablesUsed;

        logger.LogInformation(
            "Self-consistency voting: {Agree}/{Executed} candidates agreed (of {Generated} generated)",
            agreementCount, executedCount, candidates.Count);

        var note = $"*Self-consistency: {agreementCount} of {executedCount} executed candidate(s) agreed on this result set ({candidates.Count} generated).*\n\n";
        return (winner, winnerTables, note);
    }

    // Result-set majority vote. Groups the SUCCESSFULLY-executed candidates by fingerprint and
    // returns the SQL of the largest group; ties break to the first-seen group. Returns null when
    // no candidate executed successfully (caller then falls back to the single-candidate path).
    // Pure + static so it is unit-testable without an LLM or a database.
    internal static string? SelectMajority(IReadOnlyList<(string Sql, string Fingerprint, bool Ok)> candidates)
    {
        var successful = candidates
            .Where(x => x.Ok)
            .ToList();

        if (successful.Count == 0)
        {
            return null;
        }

        return successful
            .GroupBy(x => x.Fingerprint)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => successful.FindIndex(y => y.Fingerprint == x.Key))
            .First()
            .First()
            .Sql;
    }

    // Order-independent result-set fingerprint for self-consistency voting: two candidates returning the
    // same rows in a different order (no stable ORDER BY) must produce the same fingerprint so they count as
    // agreeing. QueryExecutionResult exposes only the formatted markdown table, so canonicalize by trimming
    // and ordinally sorting its non-empty lines — the header/separator lines are identical across same-shaped
    // results, so the sort is stable and only row order is neutralized. Internal for unit tests.
    internal static string ResultFingerprint(QueryExecutionResult result)
    {
        var canonical = string.Join(
            "\n",
            (result.FormattedResult ?? "")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.Ordinal));

        return $"{result.IsSuccess}|{result.RowCount}|{canonical}";
    }

    private async Task<string?> TryDryRunAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        try
        {
            var outcome = await queryExecutionService.ValidateAsync(dataSourceId, sql, ct);

            // A skipped provider dry-run (engine has no EXPLAIN strategy) checked nothing — treat it
            // like the infrastructure-failure path below: never spend a repair attempt on it.
            return outcome.Skipped ? null : outcome.Error;
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

    private string? ValidateGeneratedSql(string sql, Core.Models.McpSettingsData settings, string? dialect)
    {
        var validation = guardrailService.ValidateQuery(sql, BuildGuardrailOptions(settings));
        if (!validation.IsValid)
        {
            return validation.Error;
        }

        // AST-based read-only defense-in-depth on top of the regex guardrail (§1.5)
        return settings.EnforceReadOnly
            ? readOnlyAstValidator.Validate(sql, dialect)
            : null;
    }

    private static QueryGuardrailOptions BuildGuardrailOptions(Core.Models.McpSettingsData settings)
    {
        return new QueryGuardrailOptions
        {
            ReadOnly = settings.EnforceReadOnly,
            DetectPii = settings.EnablePiiDetection,
            CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
        };
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

    // §9.5 — audit + signal must be recorded on every outcome, including pre-generation failures
    private async Task<CallToolResult> FailAsync(
        McpSignalBuilder signal,
        Stopwatch sw,
        int? projectId,
        string question,
        string error,
        CancellationToken cancellationToken)
    {
        sw.Stop();
        signal.SetExecutionFailed(error);
        signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
        await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
            question, null, projectId, (int)sw.ElapsedMilliseconds, null, error, cancellationToken);
        await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
        return ToolHelper.Error(error);
    }

    private static bool SqlEquals(string left, string right)
    {
        return string.Equals(
            left.Trim().TrimEnd(';'),
            right.Trim().TrimEnd(';'),
            StringComparison.OrdinalIgnoreCase);
    }

    // Machine-readable companion to the markdown answer: { signal_id, generated_sql, corrected_sql?,
    // columns?, rows?, row_count?, truncated? }. The columns/rows payload is present only when the
    // single-source SQL path executed and produced rows; cross-source and execute:false carry the
    // SQL/signal fields only. Returns null when nothing is available (e.g. knowledge-path callers
    // never reach here). Internal for unit tests (InternalsVisibleTo Beacon.Tests).
    internal static JsonNode? BuildAskStructuredContent(int? signalId, string? generatedSql, string? correctedSql, JsonNode? resultPayload)
    {
        var structured = new JsonObject();
        if (signalId is { } id)
        {
            structured["signal_id"] = id;
        }

        if (generatedSql != null)
        {
            structured["generated_sql"] = generatedSql;
        }

        if (correctedSql != null)
        {
            structured["corrected_sql"] = correctedSql;
        }

        if (resultPayload is JsonObject payload)
        {
            foreach (var property in payload.ToList())
            {
                structured[property.Key] = property.Value?.DeepClone();
            }
        }

        return structured.Count > 0 ? structured : null;
    }
}

// Return value of ProjectAskTool.GenerateAndExecuteSqlAsync. ResultPayload is the structured
// { columns, rows, row_count, truncated } node from the execution that produced the answer (null when
// execution was skipped, failed, or returned no rows); GeneratedSql is the SQL shown under
// "### Generated SQL"; CorrectedSql is the repair the flow adopted, when any. The 2-arity Deconstruct
// preserves the original (Text, Succeeded) tuple shape for existing call sites and tests.
internal sealed record AskSqlOutcome(
    string Text,
    bool Succeeded,
    JsonNode? ResultPayload = null,
    string? GeneratedSql = null,
    string? CorrectedSql = null)
{
    public void Deconstruct(out string text, out bool succeeded)
    {
        text = Text;
        succeeded = Succeeded;
    }
}
