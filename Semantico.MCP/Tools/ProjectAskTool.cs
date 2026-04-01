using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Semantico.AI.Services.Knowledge;
using Semantico.AI.Services.LlmProviders;
using Semantico.AI.Services.Mcp;
using Semantico.Core.Services;
using Semantico.Core.Services.Security;
using Semantico.MCP.Services;

namespace Semantico.MCP.Tools;

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
    IQueryExecutionService queryExecutionService,
    IIntentClassifier intentClassifier,
    IDataSourceRouter dataSourceRouter,
    ISqlGenerationService sqlGenerationService,
    IKnowledgeAnswerService knowledgeAnswerService,
    ICrossSourceQueryService crossSourceQueryService,
    ILogger<ProjectAskTool> logger)
{
    [McpServerTool(Name = "ask")]
    [Description("Ask a natural language question about your data or project. For data queries, Semantico auto-detects the right data source(s), generates SQL, and executes it. For conceptual questions (e.g., 'how do notifications work?'), it answers from project documentation and knowledge base.")]
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

    private async Task<string> GenerateAndExecuteSqlAsync(
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

        var validation = guardrailService.ValidateQuery(generatedSql, new QueryGuardrailOptions
        {
            ReadOnly = settings.EnforceReadOnly,
            DetectPii = settings.EnablePiiDetection,
            CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
        });
        if (!validation.IsValid)
        {
            text += $"**Validation Error:** {validation.Error}\n";
            return text;
        }

        // Pre-execution schema validation
        var schemaCheck = schemaValidator.Validate(generatedSql, smartContext.SchemaCatalog, smartContext.DatabaseDialect);
        if (!schemaCheck.IsValid)
        {
            signal.SetSchemaValidationFailed(schemaCheck.Error!);
            logger.LogInformation("Schema pre-validation failed, retrying. Error: {Error}", schemaCheck.Error);

            var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
            var preValidationRetry = await sqlGenerationService.RetryWithErrorAsync(
                llmProvider, systemPrompt, generatedSql, schemaCheck.Error!,
                smartContext.FullContext, null, question, ct);

            if (preValidationRetry != null)
            {
                var retryGuardrail = guardrailService.ValidateQuery(preValidationRetry, new QueryGuardrailOptions
                {
                    ReadOnly = settings.EnforceReadOnly,
                    DetectPii = settings.EnablePiiDetection,
                    CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                });
                if (retryGuardrail.IsValid)
                {
                    signal.SetRetry(preValidationRetry, true);
                    text += $"*Initial query had schema errors ({schemaCheck.Error}), retried.*\n\n";
                    text += $"### Corrected SQL\n```sql\n{preValidationRetry}\n```\n\n";
                    generatedSql = preValidationRetry;
                }
            }
        }

        if (execute)
        {
            var execResult = await queryExecutionService.ExecuteAsync(dataSourceId, generatedSql, 100, ct);

            if (!execResult.IsSuccess && execResult.ErrorMessage != null)
            {
                signal.SetExecutionFailed(execResult.ErrorMessage);
                logger.LogInformation("SQL error detected, retrying. Error: {Error}", execResult.ErrorMessage);
                var tableNames = SqlParsingHelper.ExtractTableNamesFromSql(generatedSql);
                var tablesContext = tableNames.Count > 0
                    ? await knowledgeGraph.GetTablesContextAsync(dataSourceId, tableNames, ct)
                    : null;

                var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
                var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
                    llmProvider, systemPrompt, generatedSql, execResult.ErrorMessage,
                    smartContext.FullContext, tablesContext, question, ct);

                if (retriedSql != null)
                {
                    var retryValidation = guardrailService.ValidateQuery(retriedSql, new QueryGuardrailOptions
                    {
                        ReadOnly = settings.EnforceReadOnly,
                        DetectPii = settings.EnablePiiDetection,
                        CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                    });

                    if (retryValidation.IsValid)
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

            text += execResult.FormattedResult ?? $"**Execution Error:** {execResult.ErrorMessage}\n";
        }

        return text;
    }
}
