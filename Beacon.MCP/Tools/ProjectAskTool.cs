using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectAskTool(
    IKnowledgeGraphService knowledgeGraph,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider,
    IServiceProvider serviceProvider,
    IProjectContext projectContext,
    McpProjectContextManager sessionManager,
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
    [McpServerTool(Name = "ask")]
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

        if (string.IsNullOrEmpty(question))
            return ToolHelper.Error("Missing required parameter: question");

        var resolveError = ToolHelper.ResolveProjectId(projectContext, sessionManager, project_id, out var projectId);
        if (resolveError != null) return ToolHelper.Error(resolveError);

        var signal = new McpSignalBuilder()
            .SetTool("ask")
            .SetQuestion(question)
            .SetProjectId(projectId)
            .SetUserId(projectContext.UserId);

        try
        {
            var llmProvider = serviceProvider.GetService(typeof(ILlmProvider)) as ILlmProvider;
            if (llmProvider == null)
            {
                sw.Stop();
                await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                    question, null, projectId, (int)sw.ElapsedMilliseconds, null, "LLM not configured", CancellationToken.None);
                return ToolHelper.Error("AI features not configured. Add LLM configuration to use the 'ask' tool.");
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
                return ToolHelper.Error("This project has no data sources configured.");

            // Route to the right data source(s)
            var routing = await dataSourceRouter.RouteAsync(llmProvider, dataSources, question, cancellationToken);
            signal.SetRouting(routing.Sources.Select(x => (x.DataSourceId, x.DataSourceName, x.Reason)).ToList());

            var text = $"# Question: {question}\n\n";

            if (routing.Sources.Count == 0)
                return ToolHelper.Error("Could not determine which data source to query for this question.");

            // Generate and execute SQL
            if (routing.Sources.Count == 1)
            {
                var source = routing.Sources[0];
                text += $"## Data Source: {source.DataSourceName}\n";
                text += $"**Reasoning:** {source.Reason}\n\n";

                var sqlResult = await GenerateAndExecuteSqlAsync(
                    llmProvider, source.DataSourceId, question, settings, execute, signal, cancellationToken);
                text += sqlResult;
            }
            else
            {
                text += "## Cross-Source Query\n\n";
                foreach (var source in routing.Sources)
                    text += $"- **{source.DataSourceName}** (ID: {source.DataSourceId}): {source.Reason}\n";
                text += "\n";

                text += await crossSourceQueryService.ExecuteAsync(
                    llmProvider, routing.Sources, question, settings, execute, cancellationToken);
            }

            sw.Stop();
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, true);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                question, null, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
            return ToolHelper.Success(text);
        }
        catch (Exception ex)
        {
            sw.Stop();
            signal.SetExecutionFailed(ex.Message);
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "ask",
                question, null, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            await signalService.RecordSignalAsync(signal.Build(), CancellationToken.None);
            return ToolHelper.Error(ex.Message);
        }
    }

    // Internal for repair-flow tests (InternalsVisibleTo Beacon.Tests)
    internal async Task<string> GenerateAndExecuteSqlAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        string question,
        Core.Models.McpSettingsData settings,
        bool execute,
        McpSignalBuilder signal,
        CancellationToken ct)
    {
        var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(dataSourceId, question, ct);
        signal.SetDataSourceId(dataSourceId);

        var sqlResult = await sqlGenerationService.GenerateAsync(llmProvider, smartContext.FullContext, question, settings, ct);
        var generatedSql = sqlResult.Sql;
        signal.SetGeneratedSql(generatedSql, sqlResult.TablesUsed);

        var text = $"### Generated SQL\n```sql\n{generatedSql}\n```\n\n";

        var validationError = ValidateGeneratedSql(generatedSql, settings, smartContext.DatabaseDialect);
        if (validationError != null)
        {
            signal.SetExecutionFailed(validationError);
            text += $"**Validation Error:** {validationError}\n";
            return text;
        }

        // Explicit budget shared by ALL repair triggers (schema, dry-run, execution error, empty result)
        var repairAttempts = 0;
        const int maxRepairAttempts = 2;
        var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";

        // Pre-execution schema validation
        var schemaCheck = schemaValidator.Validate(generatedSql, smartContext.SchemaCatalog, smartContext.DatabaseDialect);
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
                    signal.SetRetry(preValidationRetry, true);
                    text += $"*Initial query had schema errors ({schemaCheck.Error}), retried.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{preValidationRetry}\n```\n\n";
                    generatedSql = preValidationRetry;
                }
            }
        }

        if (!execute)
        {
            return text;
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
                    signal.SetRetry(retriedSql, true);
                    text += $"*Initial query failed ({execResult.ErrorMessage}), retried with corrected SQL.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{retriedSql}\n```\n\n";
                    var retryExec = await queryExecutionService.ExecuteAsync(dataSourceId, retriedSql, 100, ct);
                    text += retryExec.FormattedResult ?? $"**Execution Error:** {retryExec.ErrorMessage}\n";
                    return text;
                }
            }
        }
        else if (execResult.IsSuccess && execResult.RowCount == 0 && repairAttempts < maxRepairAttempts && !QuestionExpectsCountOrExistence(question))
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
                        return text;
                    }

                    signal.SetRetry(emptyRetry, false);
                }
            }
        }

        text += execResult.FormattedResult ?? $"**Execution Error:** {execResult.ErrorMessage}\n";

        return text;
    }

    private async Task<string?> TryDryRunAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        try
        {
            return await queryExecutionService.ValidateAsync(dataSourceId, sql, ct);
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

    private static bool SqlEquals(string left, string right)
    {
        return string.Equals(
            left.Trim().TrimEnd(';'),
            right.Trim().TrimEnd(';'),
            StringComparison.OrdinalIgnoreCase);
    }
}
