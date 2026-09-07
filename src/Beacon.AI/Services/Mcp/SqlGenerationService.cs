using Microsoft.Extensions.Logging;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;
using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

internal sealed class SqlGenerationService(TimeProvider? timeProvider = null, ILogger<SqlGenerationService>? logger = null) : ISqlGenerationService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger<SqlGenerationService>? _logger = logger;

    public async Task<SqlGenerationResult> GenerateAsync(
        ILlmProvider llmProvider,
        string schemaContext,
        string question,
        McpSettingsData settings,
        CancellationToken ct,
        decimal? temperature = null)
    {
        var request = BuildGenerationRequest(schemaContext, question, settings, temperature ?? 0.1m);
        var response = await llmProvider.CompleteAsync(request, ct);
        return BuildResult(response);
    }

    public async Task<IReadOnlyList<SqlGenerationResult>> GenerateCandidatesAsync(
        ILlmProvider llmProvider,
        string schemaContext,
        string question,
        McpSettingsData settings,
        int candidateCount,
        decimal temperature,
        CancellationToken ct)
    {
        // Concurrent sampling — each completion funnels through LlmRequestQueue via the injected
        // provider, which bounds real concurrency (§6.1). A single unusable sample (truncated / no
        // SQL) is dropped individually rather than failing the whole vote, so the caller can still
        // fall back to the single-candidate path with whatever candidates did come back.
        var tasks = new List<Task<SqlGenerationResult?>>(candidateCount);
        for (var i = 0; i < candidateCount; i++)
        {
            tasks.Add(GenerateOneCandidateAsync(schemaContext, question, settings, temperature, llmProvider, ct));
        }

        var results = await Task.WhenAll(tasks);

        return results
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();
    }

    private async Task<SqlGenerationResult?> GenerateOneCandidateAsync(
        string schemaContext,
        string question,
        McpSettingsData settings,
        decimal temperature,
        ILlmProvider llmProvider,
        CancellationToken ct)
    {
        var request = BuildGenerationRequest(schemaContext, question, settings, temperature);

        try
        {
            var response = await llmProvider.CompleteAsync(request, ct);
            return BuildResult(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Truncated / SQL-less candidate (AiServiceException) or a failing completion call
            // (HttpRequestException, provider-specific failures, …) — dropped individually so
            // Task.WhenAll still completes with whatever candidates did come back (fail-closed,
            // 2026-07-13 lesson). §1.11 — exception type only, never the question or schema context.
            _logger?.LogWarning("SQL candidate generation failed ({ExceptionType}); dropping this candidate.", ex.GetType().Name);
            return null;
        }
    }

    private LlmRequest BuildGenerationRequest(
        string schemaContext,
        string question,
        McpSettingsData settings,
        decimal temperature)
    {
        var systemPrompt = settings.AskSystemPrompt ?? """
            You are a SQL expert. Based on the provided database schema and context, generate a SQL query to answer the user's question.

            Rules:
            - Before the SQL you MAY add a short comment block, one assumption per line, then the SQL:
                -- assumptions:
                -- "last week" = the 7 days before today
                -- revenue = SUM(order_items.quantity * order_items.unit_price)
              If the question cannot be answered without guessing between materially different readings, add one line
                -- clarification: <the single question you would ask>
              and still return your best-effort SQL. Write "-- assumptions: none" when there are none.
            - After the comment block return ONLY the SQL query, no prose.
            - Use SELECT only (read-only)
            - Use proper quoting for identifiers
            - Limit results to 100 rows unless the question implies aggregation
            - The data source header names the SQL dialect. Use only that dialect's syntax, quoting and date/time functions.
            - The schema may have two sections: "Relevant Tables" with full columns, and "Other Tables" with column summaries.
            - Columns are listed as tuples: (column_name: data_type, PK, NOT NULL, description, Examples: [v1, v2, ...]). "Examples" shows real representative values from the data — use them to match exact filter values, casing, and value formats (e.g. status codes like 'A'/'I' instead of 'active'/'inactive').
            - "Values (all N)" lists EVERY distinct value of that column — a filter on any value not listed returns zero rows. "Examples" is only a sample.
            - "Value matches" are real values found in the data for words in the question — prefer them over guessing casing or spelling.
            - Each table may list a "Foreign Keys" section (column → target_table.target_column). Use these relationships to find correct join paths.
            - CRITICAL: Use ONLY the exact column names listed in the schema. NEVER guess or infer column names. Common mistakes: using "created_at" instead of "created_time", "updated_at" instead of "modified_time", "name" instead of "title", etc. Every column in your SQL MUST appear verbatim in the schema.
            - CRITICAL: If a table is in "Other Tables" with limited columns shown, do NOT assume it has columns beyond what is listed — join through it using only its listed columns.
            - CRITICAL: Before writing your final SQL, mentally verify each column reference against the schema. If a column is not listed, do NOT use it.
            """;

        var userMessage = "";
        if (!string.IsNullOrWhiteSpace(settings.GlobalInstruction))
        {
            userMessage += $"INSTRUCTIONS:\n{settings.GlobalInstruction}\n\n";
        }

        userMessage += $"{TodayLine()}\n\n";

        userMessage += $"""
            DATABASE CONTEXT:
            {schemaContext}

            USER QUESTION: {question}
            """;

        return new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userMessage)],
            Temperature = temperature,
            MaxTokens = 2048
        };
    }

    private string TodayLine()
    {
        var today = _clock.GetUtcNow();
        return $"TODAY (UTC): {today:yyyy-MM-dd} ({today.DayOfWeek})";
    }

    private static SqlGenerationResult BuildResult(LlmResponse response)
    {
        if (response.Truncated)
        {
            throw new AiServiceException("SQL generation response was truncated at the token limit. The generated SQL is incomplete and cannot be used.");
        }

        var (assumptions, clarificationHint, remainder) = ParseLeadingComments(response.Content);
        var generatedSql = CleanSqlResponse(remainder);
        var tablesUsed = SqlTableNameExtractor.ExtractTableNames(generatedSql);

        return new SqlGenerationResult(generatedSql, tablesUsed, assumptions, clarificationHint);
    }

    /// <summary>
    /// Consumes a leading comment block (optionally after a code-fence opening line) of the form
    /// <c>-- assumptions:</c> / <c>-- "assumption text"</c> / <c>-- clarification: ...</c> and returns the
    /// parsed assumptions/clarification plus the remainder with those lines removed — the fence line, if
    /// any, is left in place so <see cref="CleanSqlResponse"/> can still strip it normally. A response with
    /// no leading <c>--</c> line yields an empty assumptions list, a null clarification, and the content
    /// unchanged (R4 — no block behaves exactly as today).
    /// </summary>
    private static (IReadOnlyList<string> Assumptions, string? ClarificationHint, string Remainder) ParseLeadingComments(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');

        var bodyStart = 0;
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            bodyStart = 1;
        }

        var assumptions = new List<string>();
        string? clarificationHint = null;
        var i = bodyStart;
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var afterDashes = trimmed[2..].Trim();
            if (afterDashes.StartsWith("assumptions:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = afterDashes["assumptions:".Length..].Trim();
                if (rest.Length > 0 && !rest.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    assumptions.Add(rest);
                }
            }
            else if (afterDashes.StartsWith("clarification:", StringComparison.OrdinalIgnoreCase))
            {
                clarificationHint = afterDashes["clarification:".Length..].Trim();
            }
            else
            {
                assumptions.Add(afterDashes);
            }

            i++;
        }

        if (i == bodyStart)
        {
            // No leading comment lines at all — return the content untouched (pre-change behaviour).
            return ([], null, content);
        }

        var remainder = string.Join("\n", lines.Take(bodyStart).Concat(lines.Skip(i)));
        return (assumptions, clarificationHint, remainder);
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

            {TodayLine()}

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
            MaxTokens = 2048
        };

        var retryResponse = await llmProvider.CompleteAsync(retryRequest, ct);
        if (retryResponse.Truncated)
        {
            // A truncated repair is unusable; null tells the caller no usable retry is available
            return null;
        }

        try
        {
            // The repair prompt allows the same leading comment block as generation; strip it here too
            // so a comment block never reaches the AST validator. Assumptions from a repair are discarded
            // (the caller only wants the cleaned SQL — RetryWithErrorAsync's signature stays string?).
            var (_, _, remainder) = ParseLeadingComments(retryResponse.Content);
            return CleanSqlResponse(remainder);
        }
        catch (AiServiceException)
        {
            // No SQL in the repair response — treat as "no usable retry" instead of failing the whole ask
            return null;
        }
    }

    private static string CleanSqlResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AiServiceException("The model returned no SQL (empty response).");
        }

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

            if (bestIdx < 0)
            {
                throw new AiServiceException("The model returned no SQL — the response contains no SELECT, WITH, or EXPLAIN statement.");
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
}
