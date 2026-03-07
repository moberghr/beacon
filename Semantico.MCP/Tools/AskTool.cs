using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Knowledge;
using Semantico.AI.Services.LlmProviders;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class AskTool(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider,
    IServiceProvider serviceProvider) : IMcpTool
{
    public string Name => "ask";
    public string Description => "Ask a natural language question about your data. Semantico uses its knowledge of your schema, documentation, and data quality to generate and execute a SQL query.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["question"] = ToolHelper.StringProp("Your question in natural language (e.g., 'How many orders were placed last week?')"),
            ["datasource_id"] = ToolHelper.IntProp("The data source ID to query"),
            ["execute"] = ToolHelper.BoolProp("Whether to execute the generated SQL (default: true)")
        },
        ["question", "datasource_id"]);

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var question = ToolHelper.GetString(arguments, "question");
        var dsId = ToolHelper.GetInt(arguments, "datasource_id");
        var execute = ToolHelper.GetBool(arguments, "execute", true);

        if (string.IsNullOrEmpty(question) || dsId == null)
            return ToolHelper.ErrorResult("Missing required parameters: question, datasource_id");

        var scopeError = ToolHelper.ValidateDataSourceAccess(session, dsId.Value);
        if (scopeError != null) return scopeError;

        try
        {
            var llmProvider = serviceProvider.GetService(typeof(ILlmProvider)) as ILlmProvider;
            if (llmProvider == null)
                return ToolHelper.ErrorResult("AI features not configured. Add LLM configuration to use the 'ask' tool.");

            // Build context from knowledge graph
            var knowledgeContext = await knowledgeGraph.GetContextForLlmAsync(dsId.Value, ct: ct);

            // Load configurable settings
            var settings = await settingsProvider.GetSettingsAsync(ct);

            var systemPrompt = settings.AskSystemPrompt ?? """
                You are a SQL expert. Based on the provided database schema and context, generate a SQL query to answer the user's question.

                Rules:
                - Return ONLY the SQL query, no explanation
                - Use SELECT only (read-only)
                - Use proper quoting for identifiers
                - Limit results to 100 rows unless the question implies aggregation
                """;

            var userMessage = "";
            if (!string.IsNullOrWhiteSpace(settings.GlobalInstruction))
                userMessage += $"INSTRUCTIONS:\n{settings.GlobalInstruction}\n\n";
            userMessage += $"""
                DATABASE CONTEXT:
                {knowledgeContext}

                USER QUESTION: {question}
                """;

            var request = new LlmRequest
            {
                SystemPrompt = systemPrompt,
                Messages = [new ChatMessage(ConversationRole.User, userMessage)],
                Temperature = 0.1m,
                MaxTokens = 1024
            };

            var response = await llmProvider.CompleteAsync(request, ct);
            var generatedSql = response.Content.Trim().Trim('`').Replace("```sql", "").Replace("```", "").Trim();
            // Strip leading language tag that LLMs sometimes include (e.g., "sql\nSELECT...")
            if (generatedSql.StartsWith("sql\n", StringComparison.OrdinalIgnoreCase))
                generatedSql = generatedSql[4..].Trim();
            if (generatedSql.StartsWith("sql ", StringComparison.OrdinalIgnoreCase))
                generatedSql = generatedSql[4..].Trim();

            var text = $"# Question: {question}\n\n";
            text += $"## Generated SQL\n```sql\n{generatedSql}\n```\n\n";

            // Validate with user-configured guardrails
            var validation = guardrailService.ValidateQuery(generatedSql, new QueryGuardrailOptions
            {
                ReadOnly = settings.EnforceReadOnly,
                DetectPii = settings.EnablePiiDetection,
                CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
            });
            if (!validation.IsValid)
            {
                text += $"**Validation Error:** {validation.Error}\n";
                return ToolHelper.TextResult(text);
            }

            if (execute)
            {
                await using var context = await contextFactory.CreateDbContextAsync(ct);
                var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dsId.Value, ct)
                    ?? throw new InvalidOperationException($"Data source {dsId} not found");

                var limitedSql = guardrailService.ApplyRowLimit(generatedSql, 100, dataSource.DatabaseEngineType?.ToString());
                var provider = providerFactory.GetProvider(dataSource.DataSourceType);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                var result = await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

                if (result.Success && result.Rows?.Count > 0)
                {
                    text += $"## Results ({result.Rows.Count} rows)\n\n";
                    var columns = result.Rows[0].Keys.ToList();
                    text += "| " + string.Join(" | ", columns) + " |\n";
                    text += "| " + string.Join(" | ", columns.Select(_ => "---")) + " |\n";
                    foreach (var row in result.Rows.Take(100))
                        text += "| " + string.Join(" | ", columns.Select(c => row.TryGetValue(c, out var v) ? (v?.ToString() ?? "NULL") : "NULL")) + " |\n";
                }
                else if (!result.Success)
                {
                    text += $"**Execution Error:** {result.ErrorMessage}\n";
                }
                else
                {
                    text += "No results returned.\n";
                }
            }

            return ToolHelper.TextResult(text);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error: {ex.Message}");
        }
    }
}
