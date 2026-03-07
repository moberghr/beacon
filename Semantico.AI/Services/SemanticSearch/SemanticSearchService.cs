using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Knowledge;
using Semantico.AI.Services.LlmProviders;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;

namespace Semantico.AI.Services.SemanticSearch;

/// <summary>
/// Translates natural language questions into SQL queries and optionally executes them
/// against the target data source, enforcing read-only guardrails throughout.
/// </summary>
internal sealed class SemanticSearchService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraphService,
    ILlmProvider llmProvider,
    IQueryGuardrailService guardrailService,
    IDataSourceProviderFactory providerFactory,
    ILogger<SemanticSearchService> logger) : ISemanticSearchService
{
    private const int _defaultMaxRows = 100;
    private const string _systemPrompt =
        """
        You are an expert SQL analyst. Your job is to translate a natural language question into a correct,
        read-only SQL SELECT query using the database schema provided. Rules:
        - Return ONLY a SELECT statement. Never use INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE, or any DDL/DML.
        - Use exact table and column names from the schema context.
        - Always qualify column names with their table alias when joining multiple tables.
        - Add a LIMIT or TOP clause to prevent returning excessive rows (default: 100).
        - After the SQL, on a new line beginning with 'Explanation:', provide a brief plain-language description of what the query does.
        - Wrap the SQL in a ```sql code block.
        """;

    /// <summary>
    /// Translates a natural language question to SQL, validates it via guardrails,
    /// and optionally executes it against the data source.
    /// </summary>
    public async Task<SemanticSearchResult> AskAsync(
        int dataSourceId,
        string question,
        bool execute = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new SemanticSearchResult(string.Empty, null, null, "Question cannot be empty");

        try
        {
            // 1. Build schema context for the LLM
            var schemaContext = await knowledgeGraphService.GetContextForLlmAsync(dataSourceId, ct: ct);

            // 2. Generate SQL via LLM
            var userPrompt = $"""
                Database schema context:
                {schemaContext}

                Question: {question}
                """;

            var llmRequest = new LlmRequest
            {
                SystemPrompt = _systemPrompt,
                Messages = new List<ChatMessage>
                {
                    new(ConversationRole.User, userPrompt)
                },
                Temperature = 0.1m,
                MaxTokens = 2048
            };

            var llmResponse = await llmProvider.CompleteAsync(llmRequest, ct);
            var (generatedSql, explanation) = ParseLlmResponse(llmResponse.Content);

            if (string.IsNullOrWhiteSpace(generatedSql))
            {
                return new SemanticSearchResult(
                    string.Empty,
                    explanation,
                    null,
                    "The model did not produce a recognisable SQL query");
            }

            logger.LogInformation(
                "SQL generated for DataSource {DataSourceId}: {Sql}",
                dataSourceId, generatedSql);

            // 3. Validate query with guardrails
            var validation = guardrailService.ValidateQuery(generatedSql, new QueryGuardrailOptions
            {
                ReadOnly = true,
                MaxRows = _defaultMaxRows,
                DetectPii = true
            });

            if (!validation.IsValid)
            {
                return new SemanticSearchResult(
                    generatedSql,
                    explanation,
                    null,
                    $"Generated query failed safety validation: {validation.Error}");
            }

            if (!execute)
                return new SemanticSearchResult(generatedSql, explanation, null, null);

            // 4. Load the data source entity
            await using var context = await contextFactory.CreateDbContextAsync(ct);

            var dataSource = await context.DataSources
                .Where(ds => ds.Id == dataSourceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

            // 5. Apply row limit and execute
            var engine = dataSource.DatabaseEngineType?.ToString();
            var limitedSql = guardrailService.ApplyRowLimit(generatedSql, _defaultMaxRows, engine);

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            var queryResult = await provider.ExecuteQueryAsync(
                dataSource,
                limitedSql,
                new Dictionary<string, object?>(),
                ct);

            if (!queryResult.Success)
            {
                return new SemanticSearchResult(
                    generatedSql,
                    explanation,
                    null,
                    queryResult.ErrorMessage ?? "Query execution failed");
            }

            // 6. Optionally mask PII columns
            var rows = queryResult.Rows;
            if (validation.PiiColumns is { Count: > 0 } piiCols && rows.Count > 0)
            {
                rows = rows
                    .Select(row => guardrailService.MaskPiiValues(row, piiCols))
                    .ToList();
            }

            logger.LogInformation(
                "Semantic search returned {RowCount} rows for DataSource {DataSourceId}",
                rows.Count, dataSourceId);

            return new SemanticSearchResult(generatedSql, explanation, rows, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Semantic search failed for DataSource {DataSourceId}", dataSourceId);
            return new SemanticSearchResult(string.Empty, null, null, ex.Message);
        }
    }

    // --- Private helpers ---

    /// <summary>
    /// Parses the LLM response to extract the SQL block and optional explanation text.
    /// </summary>
    private static (string Sql, string? Explanation) ParseLlmResponse(string content)
    {
        var sql = string.Empty;
        string? explanation = null;

        // Extract SQL from ```sql ... ``` block
        var sqlStart = content.IndexOf("```sql", StringComparison.OrdinalIgnoreCase);
        if (sqlStart >= 0)
        {
            sqlStart += 6; // skip past ```sql
            var sqlEnd = content.IndexOf("```", sqlStart, StringComparison.Ordinal);
            if (sqlEnd > sqlStart)
                sql = content[sqlStart..sqlEnd].Trim();
        }

        // Extract explanation line
        var explanationIndex = content.IndexOf("Explanation:", StringComparison.OrdinalIgnoreCase);
        if (explanationIndex >= 0)
        {
            var afterLabel = content[(explanationIndex + "Explanation:".Length)..];
            var lineEnd = afterLabel.IndexOf('\n');
            explanation = (lineEnd >= 0 ? afterLabel[..lineEnd] : afterLabel).Trim();
        }

        return (sql, explanation);
    }
}
