using System.Text.RegularExpressions;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

internal sealed class SqlGenerationService : ISqlGenerationService
{
    public async Task<SqlGenerationResult> GenerateAsync(
        ILlmProvider llmProvider,
        string schemaContext,
        string question,
        McpSettingsData settings,
        CancellationToken ct)
    {
        var systemPrompt = settings.AskSystemPrompt ?? """
            You are a SQL expert. Based on the provided database schema and context, generate a SQL query to answer the user's question.

            Rules:
            - Return ONLY the SQL query, no explanation
            - Use SELECT only (read-only)
            - Use proper quoting for identifiers
            - Limit results to 100 rows unless the question implies aggregation
            - The schema may have two sections: "Relevant Tables" with full columns, and "Other Tables" with column summaries.
            - CRITICAL: Use ONLY the exact column names listed in the schema. NEVER guess or infer column names. Common mistakes: using "created_at" instead of "created_time", "updated_at" instead of "modified_time", "name" instead of "title", etc. Every column in your SQL MUST appear verbatim in the schema.
            - CRITICAL: If a table is in "Other Tables" with limited columns shown, do NOT assume it has columns beyond what is listed — join through it using only its listed columns.
            - CRITICAL: Before writing your final SQL, mentally verify each column reference against the schema. If a column is not listed, do NOT use it.
            """;

        var userMessage = "";
        if (!string.IsNullOrWhiteSpace(settings.GlobalInstruction))
        {
            userMessage += $"INSTRUCTIONS:\n{settings.GlobalInstruction}\n\n";
        }

        userMessage += $"""
            DATABASE CONTEXT:
            {schemaContext}

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
        var tablesUsed = ExtractTableNamesFromSql(generatedSql);

        return new SqlGenerationResult(generatedSql, tablesUsed);
    }

    public async Task<string?> RetryWithErrorAsync(
        ILlmProvider llmProvider,
        string systemPrompt,
        string previousSql,
        string error,
        string fullContext,
        string? tablesContext,
        string question,
        CancellationToken ct)
    {
        var schemaSection = tablesContext != null
            ? $"""

                EXACT SCHEMAS FOR TABLES IN YOUR QUERY:
                {tablesContext}
                """
            : "";

        var retryMessage = $"""
            Your previous SQL query failed.

            PREVIOUS SQL:
            {previousSql}

            ERROR: {error}

            FULL DATABASE SCHEMA (authoritative source):
            {fullContext}
            {schemaSection}
            USER QUESTION: {question}

            CRITICAL RULES:
            - Use ONLY column names listed in the schemas above. Do NOT guess or infer column names.
            - Check every column reference against the schema before including it.
            - Use FK relationships shown in the schema to find correct join paths.
            - Ensure non-aggregate SELECT columns appear in GROUP BY when using aggregate functions.
            - Return ONLY the corrected SQL query, nothing else.
            """;

        var retryRequest = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, retryMessage)],
            Temperature = 0.1m,
            MaxTokens = 1024
        };

        var retryResponse = await llmProvider.CompleteAsync(retryRequest, ct);

        return CleanSqlResponse(retryResponse.Content);
    }

    private static string CleanSqlResponse(string content)
    {
        var sql = content.Trim();

        if (sql.Contains("```"))
        {
            var startIdx = sql.IndexOf("```", StringComparison.Ordinal);
            var afterFence = sql[(startIdx + 3)..];
            var newlineIdx = afterFence.IndexOf('\n');
            if (newlineIdx >= 0)
            {
                afterFence = afterFence[(newlineIdx + 1)..];
            }

            var endIdx = afterFence.IndexOf("```", StringComparison.Ordinal);
            sql = endIdx >= 0 ? afterFence[..endIdx].Trim() : afterFence.Trim();
        }
        else
        {
            var keywords = new[] { "SELECT ", "WITH ", "EXPLAIN " };
            var bestIdx = -1;
            foreach (var kw in keywords)
            {
                var idx = sql.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestIdx < 0 || idx < bestIdx))
                {
                    bestIdx = idx;
                }
            }

            if (bestIdx > 0)
            {
                sql = sql[bestIdx..].Trim();
            }
        }

        var lastSemicolon = sql.LastIndexOf(';');
        if (lastSemicolon >= 0)
        {
            sql = sql[..(lastSemicolon + 1)].Trim();
        }

        return sql;
    }

    private static List<string> ExtractTableNamesFromSql(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pattern = @"(?:FROM|JOIN)\s+(?:""?(\w+)""?\.)?""?(\w+)""?";
        foreach (Match match in Regex.Matches(sql, pattern, RegexOptions.IgnoreCase))
        {
            var schema = match.Groups[1].Value;
            var table = match.Groups[2].Value;
            if (table.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
                table.Equals("LATERAL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tables.Add(string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}");
        }

        return [.. tables];
    }
}
