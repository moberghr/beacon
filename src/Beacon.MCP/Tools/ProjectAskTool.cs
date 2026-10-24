using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectAskTool(
    IKnowledgeGraphService knowledgeGraph,
    IMcpSettingsProvider settingsProvider,
    ISharedSourceSettings sharedSourceSettings,
    IServiceProvider serviceProvider,
    IProjectContext projectContext,
    McpToolCallRecorder recorder,
    IAskSqlPipeline askSqlPipeline,
    IAskSqlExecutor askSqlExecutor,
    IIntentClassifier intentClassifier,
    IDataSourceRouter dataSourceRouter,
    IKnowledgeAnswerService knowledgeAnswerService,
    ICrossSourceQueryService crossSourceQueryService,
    IOptions<McpDeploymentOptions> deploymentOptions,
    McpQueryConcurrencyLimiter concurrencyLimiter,
    TimeProvider timeProvider,
    ILogger<ProjectAskTool> logger)
{
    private const string CallerCancelledMessage = "Cancelled by the caller.";

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
        // Written by McpAuditCallToolFilter after the tool returns or throws (§1.7); the signal too, on every outcome (§9.5).
        recorder.Start("ask", projectContext.UserId, question ?? "");

        var signal = new McpSignalBuilder()
            .SetTool("ask")
            .SetQuestion(question ?? "")
            .SetUserId(projectContext.UserId);
        recorder.WithSignal(signal);

        if (string.IsNullOrEmpty(question))
            return Fail(null, "Missing required parameter: question");

        var resolveError = ToolHelper.ResolveProjectId(projectContext, "ask", project_id, out var projectId);
        if (resolveError != null)
            return Fail(null, resolveError);

        signal.SetProjectId(projectId);
        recorder.WithProject(projectId);

        // Beacon:RateLimits:Ask, per caller, MCP and playground alike: refused at once and audited like every other
        // refusal (§1.7/§9.5). The limiter is registered with the MCP server; without it the application's stand-in applies.
        if (!McpRateLimiter.For(serviceProvider).Ask.TryAcquire(McpQueryConcurrencyLimiter.CallerKeyOf(projectContext), out var retryAfter))
        {
            var refusal = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                McpRateLimiter.AskRefusalFormat,
                Beacon.Core.Services.Security.RateLimitPartitions.RetryAfterSeconds(retryAfter));

            return Fail(projectId, refusal);
        }

        // After project resolution so the audit row is attributed to the authorized project and that project's
        // content-retention decision governs the stored question.
        var ceilings = deploymentOptions.Value.Ceilings ?? new McpCeilingOptions();
        var maxQuestionChars = ceilings.EffectiveMaxQuestionChars;
        if (question.Length > maxQuestionChars)
        {
            return Fail(projectId, $"Question is {question.Length} characters long; the limit is {maxQuestionChars}.");
        }

        // The whole call — LLM calls, metadata reads and SQL alike — runs under the deployment's time budget, linked to the
        // caller's own cancellation, and makes at most the deployment's number of LLM calls. Built inside the try so a
        // failure to build them is audited like any other.
        var timeoutSeconds = ceilings.EffectiveAskTimeoutSeconds;
        CancellationTokenSource? timeBudget = null;
        CancellationTokenSource? askCancellation = null;

        try
        {
            timeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), timeProvider);
            askCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeBudget.Token);
            var askToken = askCancellation.Token;
            var llmBudget = new AskLlmBudget(ceilings.EffectiveAskMaxLlmCalls);

            var llmProvider = serviceProvider.GetService(typeof(ILlmProvider)) as ILlmProvider;
            if (llmProvider == null)
            {
                return Fail(projectId, "AI features not configured. Add LLM configuration to use the 'ask' tool.");
            }

            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, askToken);

            // MaxConcurrentQueriesPerKey: refused at once, never queued, and audited like every other refusal (§1.7/§9.5).
            using var lease = concurrencyLimiter.TryAcquire(McpQueryConcurrencyLimiter.CallerKeyOf(projectContext), settings.MaxConcurrentQueriesPerKey);
            if (lease == null)
            {
                return Fail(projectId, McpQueryConcurrencyLimiter.RefusalMessage);
            }

            // Classify intent — data query vs knowledge question
            llmBudget.Consume();
            var intent = await intentClassifier.ClassifyAsync(llmProvider, question, askToken);
            signal.SetIntent(intent == IntentClassification.Knowledge ? "Knowledge" : "DataQuery");

            if (intent == IntentClassification.Knowledge)
            {
                llmBudget.Consume();
                var knowledgeResult = await knowledgeAnswerService.AnswerAsync(llmProvider, projectId, question, settings, askToken);
                recorder.Succeed();
                return ToolHelper.Success(knowledgeResult);
            }

            var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, askToken);

            if (dataSources.Count == 0)
            {
                return Fail(projectId, "This project has no data sources configured.");
            }

            // Route to the right data source(s); the router asks the LLM only when there is more than one to choose from.
            if (dataSources.Count > 1)
            {
                llmBudget.Consume();
            }

            var routing = await dataSourceRouter.RouteAsync(llmProvider, dataSources, question, askToken);
            signal.SetRouting(routing.Sources.Select(x => (x.DataSourceId, x.DataSourceName, x.Reason)).ToList());

            var text = $"# Question: {question}\n\n";

            if (routing.Sources.Count == 0)
            {
                return Fail(projectId, "Could not determine which data source to query for this question.");
            }

            // A question that spans more sources than one ask joins is refused, never answered from some of them.
            if (routing.DroppedSourceCount > 0)
            {
                return Fail(projectId, $"This question spans {routing.Sources.Count + routing.DroppedSourceCount} data sources; ask can join at most {RoutingResult.MaxSources}. Narrow the question to fewer sources.");
            }

            // SQL runs with the strictest guardrails of the live projects that link the routed data sources.
            var sourceSettings = await sharedSourceSettings.GetEffectiveSettingsAsync(
                projectId,
                routing.Sources
                    .Select(x => x.DataSourceId)
                    .Distinct()
                    .ToList(),
                askToken);

            var askSucceeded = true;
            JsonNode? resultPayload = null;
            string? generatedSql = null;
            string? correctedSql = null;

            // The audit row names the data source and the final result of a single-source answer; a cross-source answer's
            // sources and row counts are on its statements.
            int? auditedDataSourceId = null;
            int? auditedRowCount = null;
            IReadOnlyList<string>? auditedTables = null;

            // Generate and execute SQL
            if (routing.Sources.Count == 1)
            {
                var source = routing.Sources[0];
                text += $"## Data Source: {source.DataSourceName}\n";
                text += $"**Reasoning:** {source.Reason}\n\n";

                var outcome = await GenerateAndExecuteSqlAsync(
                    llmProvider, source.DataSourceId, projectId, question, sourceSettings, execute, signal, llmBudget, askToken);

                // A provider reports a cancelled statement as a failed result: the call's own cancellation ends the call.
                askToken.ThrowIfCancellationRequested();
                text += outcome.Text;
                askSucceeded = outcome.Succeeded;
                resultPayload = outcome.ResultPayload;
                generatedSql = outcome.GeneratedSql;
                correctedSql = outcome.CorrectedSql;
                auditedDataSourceId = source.DataSourceId;
                auditedRowCount = outcome.RowCount;
                auditedTables = outcome.Tables;
            }
            else
            {
                text += "## Cross-Source Query\n\n";
                foreach (var source in routing.Sources)
                    text += $"- **{source.DataSourceName}** (ID: {source.DataSourceId}): {source.Reason}\n";
                text += "\n";

                var (crossText, crossSucceeded) = await crossSourceQueryService.ExecuteAsync(
                    llmProvider, routing.Sources, projectId, question, sourceSettings, execute, signal, llmBudget, askToken);
                askToken.ThrowIfCancellationRequested();
                text += crossText;
                askSucceeded = crossSucceeded;
                // Cross-source flow builds its markdown internally; structured content for this path
                // carries signal_id + generated_sql (from the signal builder) only — no columns/rows payload.
                generatedSql = signal.GeneratedSql;
            }

            recorder
                .WithDataSource(auditedDataSourceId)
                .Succeed(auditedRowCount, auditedTables, askSucceeded, signalCountsRows: false);

            // The answer names its persisted signal for the feedback tool; the signal is written after the tool returns.
            return recorder.AnswerWithSignalId(signalId =>
                ToolHelper.Success(
                    signalId is { } id ? $"{text}\n\n_signal_id: {id}_" : text,
                    BuildAskStructuredContent(signalId, generatedSql, correctedSql, resultPayload)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation stays a cancellation; the call is still audited and signalled (§1.7/§9.5).
            recorder.FailCancelled(CallerCancelledMessage);

            throw;
        }
        catch (AskLlmBudgetExceededException ex)
        {
            return Fail(projectId, ex.Message);
        }
        catch (Exception) when (timeBudget?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            // Whatever the cancellation surfaced as (an OperationCanceledException or a provider's wrapper around one).
            return Fail(projectId, $"ask exceeded its time budget of {timeoutSeconds} s; narrow the question or ask again.");
        }
        finally
        {
            askCancellation?.Dispose();
            timeBudget?.Dispose();
        }
    }

    // Internal for repair-flow tests (InternalsVisibleTo Beacon.Tests). projectId is the caller's
    // AUTHORIZED project — project-scoped grounding (glossary, golden cases) keys on it, never on the
    // data source's own project links (codex PR-11 R4 fix). Thin wrapper: delegates the whole
    // generate → validate → repair → execute core to the shared AskSqlPipeline (§ Architecture ①) and maps
    // the structured AskSqlOutcome onto the signal builder in the same order the inline code used to emit
    // those calls, so the recorded signal (and the audit trail it feeds) is unchanged.
    internal async Task<AskToolOutcome> GenerateAndExecuteSqlAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        int projectId,
        string question,
        Core.Models.McpSettingsData settings,
        bool execute,
        McpSignalBuilder signal,
        AskLlmBudget? llmBudget,
        CancellationToken ct)
    {
        signal.SetDataSourceId(dataSourceId);

        var outcome = await askSqlPipeline.RunAsync(
            llmProvider, dataSourceId, projectId, question, settings, askSqlExecutor,
            new AskSqlPipelineOptions(Execute: execute) { LlmBudget = llmBudget }, ct);

        signal.SetGeneratedSql(outcome.GeneratedSql, outcome.TablesUsed.ToList());

        if (outcome.ColumnsUsed.Count > 0)
        {
            signal.SetColumnsUsed(outcome.ColumnsUsed);
        }

        if (outcome.ValidationError != null)
        {
            signal.SetExecutionFailed(outcome.ValidationError);
            return new AskToolOutcome(outcome.Text, false, null, outcome.GeneratedSql, null, null, outcome.TablesUsed);
        }

        // A schema, dry-run or execution failure no repair was attempted for (no repair attempt or LLM call left) marks the
        // signal exactly as a repaired one does: the same flag and the same redacted error.
        foreach (var repair in outcome.Repairs.Concat(outcome.UnrepairedFailures))
        {
            // The pipeline hands its dry-run and execution repair errors over redacted, so the stored signal never holds a
            // database error's raw text.
            switch (repair.Trigger)
            {
                case "schema":
                    signal.SetSchemaValidationFailed(repair.Error);
                    break;
                case "dry-run":
                    signal.SetDryRunFailed(repair.Error);
                    break;
                case "execution":
                    signal.SetExecutionFailed(repair.Error);
                    break;
                case "empty-result":
                    signal.SetEmptyResultRetry();
                    break;
                case "lint" when repair.RetriedSql != null:
                    signal.SetRetry(repair.RetriedSql, repair.Succeeded);
                    break;
                default:
                    // McpSignalBuilder/McpQuerySignal has no dedicated field for this trigger (e.g. a
                    // "lint" repair that was not retried) — logged so it is not silently dropped;
                    // trigger name only, never SQL or lint message text (§1.11).
                    logger.LogInformation("Repair trigger {Trigger} has no dedicated signal field", repair.Trigger);
                    break;
            }

            if (repair.RetriedSql != null)
            {
                signal.SetRetry(repair.RetriedSql, repair.Succeeded);
            }
        }

        return new AskToolOutcome(
            outcome.Text,
            outcome.Succeeded,
            outcome.Execution?.Structured,
            outcome.GeneratedSql,
            outcome.CorrectedSql,
            outcome.Execution?.RowCount,
            outcome.TablesUsed);
    }

    // §9.5 — audit + signal must be recorded on every outcome, including pre-generation failures; the recorder writes
    // the error on the signal too.
    private CallToolResult Fail(int? projectId, string error)
    {
        recorder
            .WithProject(projectId)
            .Fail(error);

        return ToolHelper.Error(error);
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
// "### Generated SQL"; CorrectedSql is the repair the flow adopted, when any; RowCount and Tables are what the audit row
// records of the final result (null when nothing executed). The 2-arity Deconstruct
// preserves the original (Text, Succeeded) tuple shape for existing call sites and tests. Named
// AskToolOutcome (not AskSqlOutcome) to avoid clashing with Beacon.AI.Services.Mcp.AskSqlOutcome.
internal sealed record AskToolOutcome(
    string Text,
    bool Succeeded,
    JsonNode? ResultPayload = null,
    string? GeneratedSql = null,
    string? CorrectedSql = null,
    int? RowCount = null,
    IReadOnlyList<string>? Tables = null)
{
    public void Deconstruct(out string text, out bool succeeded)
    {
        text = Text;
        succeeded = Succeeded;
    }
}
