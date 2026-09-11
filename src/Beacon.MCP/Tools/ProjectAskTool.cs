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
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectAskTool(
    IKnowledgeGraphService knowledgeGraph,
    IMcpSettingsProvider settingsProvider,
    IServiceProvider serviceProvider,
    IProjectContext projectContext,
    McpAuditService auditService,
    McpSignalService signalService,
    IAskSqlPipeline askSqlPipeline,
    IAskSqlExecutor askSqlExecutor,
    IIntentClassifier intentClassifier,
    IDataSourceRouter dataSourceRouter,
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

            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, cancellationToken);

            // Classify intent — data query vs knowledge question
            var intent = await intentClassifier.ClassifyAsync(llmProvider, question, cancellationToken);
            signal.SetIntent(intent == IntentClassification.Knowledge ? "Knowledge" : "DataQuery");

            if (intent == IntentClassification.Knowledge)
            {
                var knowledgeResult = await knowledgeAnswerService.AnswerAsync(llmProvider, projectId, question, settings, cancellationToken);
                sw.Stop();
                signal.SetResult(null, (int)sw.ElapsedMilliseconds, true);
                await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                    question, null, projectId, (int)sw.ElapsedMilliseconds, null, null, ct: cancellationToken);
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
                question, null, projectId, (int)sw.ElapsedMilliseconds, null, null, ct: cancellationToken);
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
                question, null, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, ct: CancellationToken.None);
            await signalService.RecordSignalAsync(signal.Build(), CancellationToken.None);
            // §1.11 — ex.Message can quote user input; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "ask", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "ask"));
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
        CancellationToken ct)
    {
        signal.SetDataSourceId(dataSourceId);

        var outcome = await askSqlPipeline.RunAsync(
            llmProvider, dataSourceId, projectId, question, settings, askSqlExecutor,
            new AskSqlPipelineOptions(Execute: execute), ct);

        signal.SetGeneratedSql(outcome.GeneratedSql, outcome.TablesUsed.ToList());

        if (outcome.ColumnsUsed.Count > 0)
        {
            signal.SetColumnsUsed(outcome.ColumnsUsed);
        }

        if (outcome.ValidationError != null)
        {
            signal.SetExecutionFailed(outcome.ValidationError);
            return new AskToolOutcome(outcome.Text, false, null, outcome.GeneratedSql, null);
        }

        foreach (var repair in outcome.Repairs)
        {
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

        return new AskToolOutcome(outcome.Text, outcome.Succeeded, outcome.Execution?.Structured, outcome.GeneratedSql, outcome.CorrectedSql);
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
            question, null, projectId, (int)sw.ElapsedMilliseconds, null, error, ct: cancellationToken);
        await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
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
// "### Generated SQL"; CorrectedSql is the repair the flow adopted, when any. The 2-arity Deconstruct
// preserves the original (Text, Succeeded) tuple shape for existing call sites and tests. Named
// AskToolOutcome (not AskSqlOutcome) to avoid clashing with Beacon.AI.Services.Mcp.AskSqlOutcome.
internal sealed record AskToolOutcome(
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
