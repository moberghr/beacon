using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Documentation;
using Semantico.AI.Services.Knowledge;
using Semantico.AI.Services.LlmProviders;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class ProjectAskTool(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider,
    IServiceProvider serviceProvider,
    ILogger<ProjectAskTool> logger) : IMcpTool
{
    public string Name => "ask";
    public string Description => "Ask a natural language question about your data or project. For data queries, Semantico auto-detects the right data source(s), generates SQL, and executes it. For conceptual questions (e.g., 'how do notifications work?'), it answers from project documentation and knowledge base.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["question"] = ToolHelper.StringProp("Your question in natural language (e.g., 'How many orders were placed last week?')"),
            ["project_id"] = ToolHelper.IntProp("Optional. Specify project if your API key has access to multiple projects."),
            ["execute"] = ToolHelper.BoolProp("Whether to execute the generated SQL (default: true)")
        },
        ["question"]);

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var question = ToolHelper.GetString(arguments, "question");
        var requestedProjectId = ToolHelper.GetInt(arguments, "project_id");
        var execute = ToolHelper.GetBool(arguments, "execute", true);

        if (string.IsNullOrEmpty(question))
            return ToolHelper.ErrorResult("Missing required parameter: question");

        var resolveError = ToolHelper.ResolveProjectId(session, requestedProjectId, out var projectId);
        if (resolveError != null) return resolveError;

        try
        {
            var llmProvider = serviceProvider.GetService(typeof(ILlmProvider)) as ILlmProvider;
            if (llmProvider == null)
                return ToolHelper.ErrorResult("AI features not configured. Add LLM configuration to use the 'ask' tool.");

            var settings = await settingsProvider.GetSettingsAsync(ct);

            // Phase 0: Classify intent — data query vs knowledge question
            var intent = await ClassifyIntentAsync(llmProvider, question, ct);

            if (intent == QuestionIntent.Knowledge)
                return await AnswerFromKnowledgeAsync(llmProvider, projectId, question, settings, ct);

            var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, ct);

            if (dataSources.Count == 0)
                return ToolHelper.ErrorResult("This project has no data sources configured.");

            // Phase 1: Route to the right data source(s)
            var routing = await RouteQuestionAsync(llmProvider, dataSources, question, projectId, settings, ct);

            var text = $"# Question: {question}\n\n";

            if (routing.Sources.Count == 0)
                return ToolHelper.ErrorResult("Could not determine which data source to query for this question.");

            // Phase 2: Generate and execute SQL for each source
            if (routing.Sources.Count == 1)
            {
                // Single source — straightforward
                var source = routing.Sources[0];
                text += $"## Data Source: {source.DataSourceName}\n";
                text += $"**Reasoning:** {source.Reason}\n\n";

                var sqlResult = await GenerateAndExecuteSqlAsync(
                    llmProvider, source.DataSourceId, question, settings, execute, ct);
                text += sqlResult;
            }
            else
            {
                // Cross-source — generate SQL per source, join in SQLite
                text += "## Cross-Source Query\n\n";
                foreach (var source in routing.Sources)
                    text += $"- **{source.DataSourceName}** (ID: {source.DataSourceId}): {source.Reason}\n";
                text += "\n";

                text += await ExecuteCrossSourceQueryAsync(
                    llmProvider, routing.Sources, question, settings, execute, ct);
            }

            return ToolHelper.TextResult(text);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error: {ex.Message}");
        }
    }

    private async Task<RoutingResult> RouteQuestionAsync(
        ILlmProvider llmProvider,
        List<DataSourceKnowledge> dataSources,
        string question,
        int projectId,
        Core.Models.McpSettingsData settings,
        CancellationToken ct)
    {
        // If single data source, skip routing LLM call
        if (dataSources.Count == 1)
        {
            return new RoutingResult
            {
                Sources =
                [
                    new RoutedSource
                    {
                        DataSourceId = dataSources[0].DataSourceId,
                        DataSourceName = dataSources[0].Name,
                        Reason = "Only data source in this project."
                    }
                ]
            };
        }

        // Build data source summaries for routing
        var summaries = string.Join("\n\n", dataSources.Select(ds =>
        {
            var schemas = string.Join(", ", ds.Schemas.Select(s => $"{s.SchemaName} ({s.TableCount} tables)"));
            return $"Data Source ID: {ds.DataSourceId}\nName: {ds.Name}\nType: {ds.DatabaseEngine ?? ds.DataSourceType.ToString()}\nTables: {ds.TableCount}\nSchemas: {schemas}";
        }));

        var routingPrompt = $$"""
            You are a data routing expert. Given the following data sources and a user question, determine which data source(s) should be queried.

            DATA SOURCES:
            {{summaries}}

            USER QUESTION: {{question}}

            Respond with JSON only, no markdown:
            {
              "sources": [
                { "datasource_id": <id>, "datasource_name": "<name>", "reason": "<why this source>" }
              ]
            }

            Rules:
            - Pick the minimum number of sources needed to answer the question.
            - Most questions need only one source.
            - Only use multiple sources if the question explicitly involves data from different domains/systems.
            """;

        var request = new LlmRequest
        {
            SystemPrompt = "You are a data routing assistant. Respond with JSON only.",
            Messages = [new ChatMessage(ConversationRole.User, routingPrompt)],
            Temperature = 0.0m,
            MaxTokens = 512
        };

        var response = await llmProvider.CompleteAsync(request, ct);
        var json = response.Content.Trim();
        // Strip markdown code fences if present
        if (json.StartsWith("```")) json = json[(json.IndexOf('\n') + 1)..];
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
        json = json.Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<RoutingResponse>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            if (parsed?.Sources == null || parsed.Sources.Count == 0)
            {
                // Fallback: use the first data source
                return new RoutingResult
                {
                    Sources = [new RoutedSource
                    {
                        DataSourceId = dataSources[0].DataSourceId,
                        DataSourceName = dataSources[0].Name,
                        Reason = "Default selection (routing could not determine specific source)."
                    }]
                };
            }

            return new RoutingResult
            {
                Sources = parsed.Sources.Select(s => new RoutedSource
                {
                    DataSourceId = s.DatasourceId,
                    DataSourceName = s.DatasourceName ?? dataSources.FirstOrDefault(ds => ds.DataSourceId == s.DatasourceId)?.Name ?? "Unknown",
                    Reason = s.Reason ?? ""
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse routing response: {Json}", json);
            // Fallback
            return new RoutingResult
            {
                Sources = [new RoutedSource
                {
                    DataSourceId = dataSources[0].DataSourceId,
                    DataSourceName = dataSources[0].Name,
                    Reason = "Default selection (routing parse error)."
                }]
            };
        }
    }

    private async Task<string> GenerateAndExecuteSqlAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        string question,
        Core.Models.McpSettingsData settings,
        bool execute,
        CancellationToken ct)
    {
        var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(dataSourceId, question, ct);

        var systemPrompt = settings.AskSystemPrompt ?? """
            You are a SQL expert. Based on the provided database schema and context, generate a SQL query to answer the user's question.

            Rules:
            - Return ONLY the SQL query, no explanation
            - Use SELECT only (read-only)
            - Use proper quoting for identifiers
            - Limit results to 100 rows unless the question implies aggregation
            - The schema may have two sections: "Relevant Tables" with full columns, and "Other Tables" with names/PKs only.
            - Use exact column names from the schema. Do not guess column names.
            """;

        var userMessage = "";
        if (!string.IsNullOrWhiteSpace(settings.GlobalInstruction))
            userMessage += $"INSTRUCTIONS:\n{settings.GlobalInstruction}\n\n";
        userMessage += $"""
            DATABASE CONTEXT:
            {smartContext.FullContext}

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
        var generatedSql = CleanSqlResponse(response.Content);

        var text = $"### Generated SQL\n```sql\n{generatedSql}\n```\n\n";

        // Validate
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

        if (execute)
        {
            var (result, errorMessage) = await ExecuteQueryWithErrorAsync(dataSourceId, generatedSql, settings, ct);

            if (errorMessage != null && IsSchemaError(errorMessage))
            {
                // Retry: extract table names from SQL, fetch full schemas, re-prompt
                logger.LogInformation("Schema error detected, retrying with full table context. Error: {Error}", errorMessage);
                var tableNames = ExtractTableNamesFromSql(generatedSql);
                if (tableNames.Count > 0)
                {
                    var tablesContext = await knowledgeGraph.GetTablesContextAsync(dataSourceId, tableNames, ct);

                    var retryMessage = $"""
                        Your previous SQL query failed. Here are the correct schemas for the tables involved.

                        PREVIOUS SQL:
                        {generatedSql}

                        ERROR: {errorMessage}

                        CORRECT TABLE SCHEMAS:
                        {tablesContext}

                        USER QUESTION: {question}

                        Generate a corrected SQL query. Return ONLY the SQL.
                        """;

                    var retryRequest = new LlmRequest
                    {
                        SystemPrompt = systemPrompt,
                        Messages = [new ChatMessage(ConversationRole.User, retryMessage)],
                        Temperature = 0.1m,
                        MaxTokens = 1024
                    };

                    var retryResponse = await llmProvider.CompleteAsync(retryRequest, ct);
                    var retriedSql = CleanSqlResponse(retryResponse.Content);

                    var retryValidation = guardrailService.ValidateQuery(retriedSql, new QueryGuardrailOptions
                    {
                        ReadOnly = settings.EnforceReadOnly,
                        DetectPii = settings.EnablePiiDetection,
                        CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                    });

                    if (retryValidation.IsValid)
                    {
                        text += $"*Initial query failed ({errorMessage}), retried with corrected schema.*\n\n";
                        text += $"### Corrected SQL\n```sql\n{retriedSql}\n```\n\n";
                        var retryResult = await ExecuteQueryOnSourceAsync(dataSourceId, retriedSql, settings, ct);
                        text += retryResult;
                        return text;
                    }
                }
            }

            text += result ?? $"**Execution Error:** {errorMessage}\n";
        }

        return text;
    }

    private async Task<(string? Result, string? ErrorMessage)> ExecuteQueryWithErrorAsync(
        int dataSourceId, string sql, Core.Models.McpSettingsData settings, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var limitedSql = guardrailService.ApplyRowLimit(sql, 100, dataSource.DatabaseEngineType?.ToString());
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

        if (result.Success && result.Rows?.Count > 0)
        {
            var text = $"### Results ({result.Rows.Count} rows)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(result.Rows);
            return (text, null);
        }

        if (!result.Success)
            return (null, result.ErrorMessage);

        return ("No results returned.\n", null);
    }

    internal static bool IsSchemaError(string errorMessage)
    {
        var msg = errorMessage;
        // PostgreSQL error codes
        if (msg.Contains("42703") || msg.Contains("42P01")) return true;
        // SQL Server
        if (msg.Contains("Invalid column name") || msg.Contains("Invalid object name")) return true;
        // Generic
        if (msg.Contains("does not exist") || msg.Contains("column") && msg.Contains("not found"))
            return true;
        return false;
    }

    internal static List<string> ExtractTableNamesFromSql(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Match FROM/JOIN followed by optional schema-qualified table name
        var pattern = @"(?:FROM|JOIN)\s+(?:""?(\w+)""?\.)?""?(\w+)""?";
        foreach (Match match in Regex.Matches(sql, pattern, RegexOptions.IgnoreCase))
        {
            var schema = match.Groups[1].Value;
            var table = match.Groups[2].Value;
            // Skip SQL keywords that might follow FROM/JOIN in subqueries
            if (table.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
                table.Equals("LATERAL", StringComparison.OrdinalIgnoreCase))
                continue;
            tables.Add(string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}");
        }
        return [.. tables];
    }

    private async Task<string> ExecuteQueryOnSourceAsync(
        int dataSourceId, string sql, Core.Models.McpSettingsData settings, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var limitedSql = guardrailService.ApplyRowLimit(sql, 100, dataSource.DatabaseEngineType?.ToString());
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

        if (result.Success && result.Rows?.Count > 0)
        {
            var text = $"### Results ({result.Rows.Count} rows)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(result.Rows);
            return text;
        }
        else if (!result.Success)
        {
            return $"**Execution Error:** {result.ErrorMessage}\n";
        }

        return "No results returned.\n";
    }

    private async Task<string> ExecuteCrossSourceQueryAsync(
        ILlmProvider llmProvider,
        List<RoutedSource> sources,
        string question,
        Core.Models.McpSettingsData settings,
        bool execute,
        CancellationToken ct)
    {
        var text = "";

        // Generate SQL for each source
        var sourceQueries = new List<(RoutedSource Source, string Sql)>();
        foreach (var source in sources)
        {
            var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(source.DataSourceId, question, ct);

            var prompt = $"""
                Generate a SQL query for this data source to contribute to answering: "{question}"

                This query's results will be stored as "result{sourceQueries.Count + 1}" and joined with results from other data sources.
                Return ONLY the SQL, no explanation. Use SELECT only.
                Use exact column names from the schema. Do not guess column names.

                DATABASE CONTEXT:
                {smartContext.FullContext}
                """;

            var request = new LlmRequest
            {
                SystemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.",
                Messages = [new ChatMessage(ConversationRole.User, prompt)],
                Temperature = 0.1m,
                MaxTokens = 1024
            };

            var response = await llmProvider.CompleteAsync(request, ct);
            var sql = CleanSqlResponse(response.Content);
            sourceQueries.Add((source, sql));

            text += $"### Source: {source.DataSourceName}\n```sql\n{sql}\n```\n\n";
        }

        if (!execute)
            return text + "*Execution skipped (execute=false)*\n";

        // Execute each source query and load into in-memory SQLite
        var loggerFactory = serviceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        using var memDb = new InMemoryDatabaseManager(loggerFactory!.CreateLogger<InMemoryDatabaseManager>());

        for (var i = 0; i < sourceQueries.Count; i++)
        {
            var (source, sql) = sourceQueries[i];
            var validation = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
            {
                ReadOnly = settings.EnforceReadOnly,
                DetectPii = settings.EnablePiiDetection,
                CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
            });
            if (!validation.IsValid)
            {
                text += $"**Validation Error for {source.DataSourceName}:** {validation.Error}\n";
                return text;
            }

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == source.DataSourceId, ct)
                ?? throw new InvalidOperationException($"Data source {source.DataSourceId} not found");

            var limitedSql = guardrailService.ApplyRowLimit(sql, 500, dataSource.DatabaseEngineType?.ToString());
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
            {
                text += $"**Execution Error for {source.DataSourceName}:** {result.ErrorMessage}\n";
                return text;
            }

            if (result.Rows?.Count > 0)
            {
                var tableName = $"result{i + 1}";
                await memDb.CreateTableFromResults(tableName, result.Rows.Cast<IDictionary<string, object?>>().ToList(), new ProjectInfo
                {
                    Name = source.DataSourceName,
                    DatabaseEngine = dataSource.DatabaseEngineType?.ToString() ?? "Unknown",
                    DatabaseEngineType = dataSource.DatabaseEngineType ?? DatabaseEngineType.PostgreSQL
                });
                text += $"**{source.DataSourceName}:** {result.Rows.Count} rows loaded as `{tableName}`\n";
            }
            else
            {
                text += $"**{source.DataSourceName}:** No results\n";
            }
        }

        // Generate the join query
        var analysis = memDb.AnalyzeDatabase();
        var tablesInfo = string.Join("\n", analysis.Tables.Values.Select(t =>
            $"Table: {t.TableName} ({t.RowCount} rows, {t.ColumnCount} columns, from {t.SourceProject})"));

        var joinPrompt = $"""
            I have the following in-memory SQLite tables loaded from different data sources:
            {tablesInfo}

            Write a SQLite query to combine these results and answer: "{question}"
            Use the table names exactly as shown (result1, result2, etc.).
            Return ONLY the SQL query.
            """;

        var joinRequest = new LlmRequest
        {
            SystemPrompt = "You are a SQLite expert. Return ONLY the SQL query.",
            Messages = [new ChatMessage(ConversationRole.User, joinPrompt)],
            Temperature = 0.1m,
            MaxTokens = 1024
        };

        var joinResponse = await llmProvider.CompleteAsync(joinRequest, ct);
        var joinSql = CleanSqlResponse(joinResponse.Content);
        var translatedSql = memDb.TranslateFinalQuery(joinSql);

        text += $"\n### Join Query\n```sql\n{translatedSql}\n```\n\n";

        var (joinResults, execTimeMs, timedOut) = await memDb.ExecuteQueryAsync(translatedSql, 30);

        if (timedOut)
        {
            text += "**Error:** Join query timed out.\n";
        }
        else if (joinResults.Count > 0)
        {
            text += $"### Final Results ({joinResults.Count} rows, {execTimeMs:F0}ms)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(joinResults);
        }
        else
        {
            text += "No results from join query.\n";
        }

        return text;
    }

    private enum QuestionIntent { DataQuery, Knowledge }

    private static async Task<QuestionIntent> ClassifyIntentAsync(
        ILlmProvider llmProvider, string question, CancellationToken ct)
    {
        var classifyPrompt = """
            Classify the following user question into one of two categories:

            DATA_QUERY — The user wants to retrieve, count, aggregate, or analyze actual data from a database.
            Examples: "How many orders last week?", "Show top customers by revenue", "What's the average response time?", "Create a diagram of usage over 30 days"

            KNOWLEDGE — The user wants to understand how something works, what something is, or learn about the system/project/architecture/processes.
            Examples: "How do notifications work?", "What is the purpose of the subscriptions table?", "Explain the data quality scoring", "What data sources are available?"

            Question: "{question}"

            Respond with exactly one word: DATA_QUERY or KNOWLEDGE
            """;

        var request = new LlmRequest
        {
            SystemPrompt = "You are a question classifier. Respond with exactly one word.",
            Messages = [new ChatMessage(ConversationRole.User, classifyPrompt.Replace("{question}", question))],
            Temperature = 0.0m,
            MaxTokens = 16
        };

        var response = await llmProvider.CompleteAsync(request, ct);
        var result = response.Content.Trim().ToUpperInvariant();

        return result.Contains("KNOWLEDGE") ? QuestionIntent.Knowledge : QuestionIntent.DataQuery;
    }

    private async Task<McpToolResult> AnswerFromKnowledgeAsync(
        ILlmProvider llmProvider, int projectId, string question,
        Core.Models.McpSettingsData settings, CancellationToken ct)
    {
        // Gather knowledge context: project documentation + search results
        var docService = serviceProvider.GetService(typeof(IProjectDocumentationService)) as IProjectDocumentationService;

        var projectContextTask = knowledgeGraph.GetProjectContextForLlmAsync(projectId, ct);
        var searchTask = knowledgeGraph.SearchProjectAsync(question, projectId, 10, ct);
        var docTask = docService?.ExportLatestToMarkdownAsync(projectId, ct);

        await Task.WhenAll(
            projectContextTask,
            searchTask,
            docTask ?? Task.CompletedTask);

        var projectContext = projectContextTask.Result;
        var searchResults = searchTask.Result;
        var documentation = docTask?.Result;

        // Build context for the LLM
        var context = "";

        if (!string.IsNullOrEmpty(documentation))
        {
            // Trim large docs to avoid token overflow — use first ~4000 chars + relevant sections
            context += "## Project Documentation\n\n";
            context += documentation.Length > 6000 ? documentation[..6000] + "\n\n[... truncated ...]\n" : documentation;
            context += "\n\n";
        }

        if (!string.IsNullOrEmpty(projectContext))
        {
            context += "## Project Schema & Data Sources\n\n";
            context += projectContext + "\n\n";
        }

        if (searchResults.Count > 0)
        {
            context += "## Relevant Search Results\n\n";
            foreach (var result in searchResults)
            {
                context += $"- **{result.SchemaName}.{result.TableName}** ({result.DataSourceName})";
                if (!string.IsNullOrEmpty(result.Description))
                    context += $": {result.Description}";
                context += "\n";
            }
            context += "\n";
        }

        if (string.IsNullOrWhiteSpace(context))
            return ToolHelper.ErrorResult("No documentation or knowledge base available for this project. Generate project documentation first.");

        var systemPrompt = """
            You are a knowledgeable assistant for a data project. Answer the user's question based on the provided project documentation, schema information, and knowledge base.

            Rules:
            - Answer based on the provided context only — do not make up information
            - Be clear and concise
            - Reference specific tables, schemas, or data sources when relevant
            - If the context doesn't contain enough information to fully answer, say what you do know and suggest what documentation or data the user might need
            """;

        var userMessage = "";
        if (!string.IsNullOrWhiteSpace(settings.GlobalInstruction))
            userMessage += $"INSTRUCTIONS:\n{settings.GlobalInstruction}\n\n";
        userMessage += $"CONTEXT:\n{context}\n\nQUESTION: {question}";

        var request = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userMessage)],
            Temperature = 0.3m,
            MaxTokens = 2048
        };

        var response = await llmProvider.CompleteAsync(request, ct);

        var text = $"# {question}\n\n";
        text += response.Content;

        return ToolHelper.TextResult(text);
    }

    private static string CleanSqlResponse(string content)
    {
        var sql = content.Trim();

        // Strip markdown code fences
        if (sql.Contains("```"))
        {
            var startIdx = sql.IndexOf("```", StringComparison.Ordinal);
            var afterFence = sql[(startIdx + 3)..];
            // Skip language identifier (e.g., "sql\n")
            var newlineIdx = afterFence.IndexOf('\n');
            if (newlineIdx >= 0)
                afterFence = afterFence[(newlineIdx + 1)..];
            var endIdx = afterFence.IndexOf("```", StringComparison.Ordinal);
            sql = endIdx >= 0 ? afterFence[..endIdx].Trim() : afterFence.Trim();
        }
        else
        {
            // No code fences — extract the SQL statement from any surrounding text
            // Find the first SQL keyword that starts a statement
            var keywords = new[] { "SELECT ", "WITH ", "EXPLAIN " };
            var bestIdx = -1;
            foreach (var kw in keywords)
            {
                var idx = sql.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestIdx < 0 || idx < bestIdx))
                    bestIdx = idx;
            }

            if (bestIdx > 0)
                sql = sql[bestIdx..].Trim();
        }

        // Remove trailing non-SQL text after the final semicolon
        var lastSemicolon = sql.LastIndexOf(';');
        if (lastSemicolon >= 0)
            sql = sql[..(lastSemicolon + 1)].Trim();

        return sql;
    }

    // Internal models for routing
    private record RoutingResult
    {
        public List<RoutedSource> Sources { get; init; } = [];
    }

    private record RoutedSource
    {
        public int DataSourceId { get; init; }
        public string DataSourceName { get; init; } = "";
        public string Reason { get; init; } = "";
    }

    private record RoutingResponse
    {
        public List<RoutingSourceEntry>? Sources { get; init; }
    }

    private record RoutingSourceEntry
    {
        public int DatasourceId { get; init; }
        public string? DatasourceName { get; init; }
        public string? Reason { get; init; }
    }
}
