using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Services.Learning;

/// <summary>
/// LLM-primary, MAGIC-style lesson extractor (§ Architecture ⑦). Given a single schema-correction
/// <see cref="FailureCluster"/> it asks the model to diagnose the failure and emit a compact structured
/// JSON lesson, which is parsed defensively into an <see cref="ExtractedLesson"/>. Every completion goes
/// through the queue-backed <see cref="ILlmProvider"/> (§6.1) and stays provider-agnostic (§9.4). Only
/// the cluster's text fields are sent — never bulk result rows (§1.11). On ANY provider/parse failure it
/// logs a warning and returns <c>null</c> so <see cref="McpLearningAggregationService"/> falls back to the
/// deterministic regex path; cancellation is always propagated (lesson: never swallow cancellation).
/// </summary>
internal sealed class LlmLessonExtractor(
    ILlmProvider llmProvider,
    ILogger<LlmLessonExtractor> logger) : ILessonExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SystemPrompt = """
        You are a database expert diagnosing why a text-to-SQL query failed and was then corrected.
        You are given a single failure cluster: the user's question, the SQL that failed, the database
        error, the corrected SQL that succeeded, and (optionally) a schema slice. No result rows are
        shown to you — reason only from the text provided.

        Produce a single durable LESSON that would prevent this class of failure in future SQL generation
        for this table. Be specific and actionable; refer to concrete schema/column names.

        Respond with EXACTLY ONE compact JSON object and NOTHING else (no markdown, no prose, no code
        fences). Use these keys:
        {
          "patternType": "SchemaCorrection",
          "patternContent": "one-line rule stored as the learned pattern, e.g. NEVER use 'created_at' on public.loans — correct column is 'created_time'",
          "exampleQuestion": "the representative question (or null)",
          "exampleSql": "the corrected SQL that succeeded (or null)",
          "symptom": "what went wrong, observably",
          "rootCause": "why it went wrong",
          "rule": "the general rule to apply",
          "exampleFix": "a concrete before/after fix (or null)",
          "applicableWhen": "the condition under which this lesson applies (or null)"
        }
        patternType must be one of: SchemaCorrection, ColumnClarification, JoinPattern, CommonQuery,
        BusinessTermMapping, DocumentationGap. Default to SchemaCorrection.
        """;

    public bool IsAvailable => true;

    public async Task<ExtractedLesson?> ExtractAsync(FailureCluster cluster, CancellationToken ct)
    {
        try
        {
            var request = new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                Messages = [new ChatMessage(ConversationRole.User, BuildUserMessage(cluster))],
                Temperature = 0.0m,
                MaxTokens = 512
            };

            var response = await llmProvider.CompleteAsync(request, ct);
            return Parse(response.Content, cluster);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Lesson extraction failed for {Schema}.{Table}; falling back to deterministic detection",
                cluster.SchemaName, cluster.TableName);
            return null;
        }
    }

    private ExtractedLesson? Parse(string? content, FailureCluster cluster)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var json = ExtractJsonObject(content);
        if (json == null)
        {
            logger.LogWarning(
                "Lesson extractor: no JSON object in LLM response for {Schema}.{Table}",
                cluster.SchemaName, cluster.TableName);
            return null;
        }

        LessonDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LessonDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Lesson extractor: unparseable LLM JSON for {Schema}.{Table}",
                cluster.SchemaName, cluster.TableName);
            return null;
        }

        if (dto == null)
        {
            return null;
        }

        var patternContent = NullIfBlank(dto.PatternContent) ?? NullIfBlank(dto.Rule);
        if (patternContent == null)
        {
            logger.LogWarning(
                "Lesson extractor: LLM returned no usable pattern content for {Schema}.{Table}",
                cluster.SchemaName, cluster.TableName);
            return null;
        }

        var patternType = Enum.TryParse<McpPatternType>(dto.PatternType, ignoreCase: true, out var parsed)
            ? parsed
            : McpPatternType.SchemaCorrection;

        return new ExtractedLesson(
            patternType,
            patternContent,
            NullIfBlank(dto.ExampleQuestion),
            NullIfBlank(dto.ExampleSql),
            dto.Symptom ?? string.Empty,
            dto.RootCause ?? string.Empty,
            dto.Rule ?? string.Empty,
            NullIfBlank(dto.ExampleFix),
            NullIfBlank(dto.ApplicableWhen));
    }

    private static string BuildUserMessage(FailureCluster cluster)
    {
        var builder = new StringBuilder();
        builder.Append("TABLE: ").Append(cluster.SchemaName).Append('.').AppendLine(cluster.TableName);
        if (!string.IsNullOrWhiteSpace(cluster.ColumnName))
        {
            builder.Append("SUSPECT COLUMN: ").AppendLine(cluster.ColumnName);
        }

        builder.Append("QUESTION: ").AppendLine(cluster.Question);
        if (!string.IsNullOrWhiteSpace(cluster.GeneratedSql))
        {
            builder.AppendLine("FAILED SQL:").AppendLine(cluster.GeneratedSql);
        }

        if (!string.IsNullOrWhiteSpace(cluster.Error))
        {
            builder.AppendLine("DATABASE ERROR:").AppendLine(cluster.Error);
        }

        if (!string.IsNullOrWhiteSpace(cluster.CorrectedSql))
        {
            builder.AppendLine("CORRECTED SQL (succeeded):").AppendLine(cluster.CorrectedSql);
        }

        if (!string.IsNullOrWhiteSpace(cluster.SchemaSlice))
        {
            builder.AppendLine("SCHEMA:").AppendLine(cluster.SchemaSlice);
        }

        return builder.ToString();
    }

    // Tolerates code fences / surrounding prose by scanning for the first balanced { ... } object,
    // ignoring braces inside string literals. Returns null when there is no JSON object at all.
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed class LessonDto
    {
        public string? PatternType { get; set; }
        public string? PatternContent { get; set; }
        public string? ExampleQuestion { get; set; }
        public string? ExampleSql { get; set; }
        public string? Symptom { get; set; }
        public string? RootCause { get; set; }
        public string? Rule { get; set; }
        public string? ExampleFix { get; set; }
        public string? ApplicableWhen { get; set; }
    }
}
