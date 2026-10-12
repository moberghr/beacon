using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.SemanticSearch;

/// <summary>
/// Translates natural language questions into SQL queries and optionally executes them
/// against the target data source, enforcing read-only guardrails throughout.
/// </summary>
internal sealed class SemanticSearchService(
    IDbContextFactory<BeaconContext> contextFactory,
    IKnowledgeGraphService knowledgeGraphService,
    ILlmProvider llmProvider,
    IQueryGuardrailService guardrailService,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IDataSourceProviderFactory providerFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    ILogger<SemanticSearchService> logger) : ISemanticSearchService
{
    /// <summary>What a failed answer says: the failure's own text stays in the server log.</summary>
    public const string FailedMessage = "The question could not be answered; try again or rephrase it.";

    private const int _defaultMaxRows = 100;

    // How long the generated statement may run, as the MCP query tool allows.
    private static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);
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
        int projectId,
        string question,
        bool execute = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new SemanticSearchResult(string.Empty, null, null, "Question cannot be empty");

        // Generating and running SQL from a question is the Query action, in the caller's project; refused before any
        // LLM call or SQL.
        await DemandQueryAsync(projectId, ct);

        try
        {
            // 1. Build schema context for the LLM, from the caller's project only (a source it does not link is not found)
            var schemaContext = await knowledgeGraphService.GetContextForLlmAsync(dataSourceId, projectId, ct: ct);

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

            // Never log the raw generated SQL — it may embed user question text / column names (§1.11).
            logger.LogInformation(
                "SQL generated for DataSource {DataSourceId} ({SqlLength} chars)",
                dataSourceId, generatedSql.Length);

            // 3. Load the data source: the gate judges the SQL in its dialect.
            await using var context = await contextFactory.CreateDbContextAsync(ct);

            var dataSource = await context.DataSources
                .Where(x => x.Id == dataSourceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

            // 4. The shared execution gate (§1.5): the regex guardrail and the read-only AST in the source's dialect, names
            // in the source's own configured database only, the host policy, PII columns, and the row cap written into the
            // SQL — a bound of the statement's own above it is lowered.
            var report = gate.Evaluate(new SqlGateRequest(
                generatedSql,
                DataSourceSqlDialect.Of(dataSource),
                EnforceReadOnly: true,
                DetectPii: true,
                CustomPiiPatterns: null,
                Catalog: null,
                BlockOnSchemaFailure: false,
                LintContext: null,
                EnableSemanticLint: false,
                MaxRows: _defaultMaxRows)
            {
                HostManagedKey = dataSource.HostManagedKey,
                ConfiguredDatabase = configuredDatabases.Resolve(dataSource)
            });
            if (report.Blocked)
            {
                return new SemanticSearchResult(
                    generatedSql,
                    explanation,
                    null,
                    $"Generated query failed safety validation: {report.BlockReason}");
            }

            if (!execute)
                return new SemanticSearchResult(generatedSql, explanation, null, null);

            // 5. §1.5 backstop — the gate's SQL on the read-only path (a READ ONLY transaction where the engine has one;
            // the warehouse connectors' own binding verdict otherwise), within a bounded statement timeout.
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(StatementTimeout);
            var queryResult = await provider.ExecuteReadOnlyQueryAsync(
                dataSource,
                report.FinalSql,
                new Dictionary<string, object?>(),
                timeoutCts.Token);

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
            if (report.PiiColumns is { Count: > 0 } piiCols && rows.Count > 0)
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
        catch (Exception ex) when (ex is BeaconException or InvalidOperationException or OperationCanceledException)
        {
            // A refusal (a data source the project does not link, a missing source) or a cancellation reaches the caller
            // as it is, never as a failed answer.
            throw;
        }
        catch (Exception ex)
        {
            // The exception's text can quote the database or the model (§1.11): logged, never returned.
            logger.LogError(ex, "Semantic search failed for DataSource {DataSourceId}", dataSourceId);
            return new SemanticSearchResult(string.Empty, null, null, FailedMessage);
        }
    }

    // --- Private helpers ---

    private async Task DemandQueryAsync(int projectId, CancellationToken ct)
    {
        var scope = await projectAccess.GetScopeAsync(ct);
        if (scope.Allows(projectId, ProjectAction.Query))
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(ct);
        if (!scope.Allows(projectId, ProjectAction.Read))
        {
            AccessRefusal.Hidden(logger, actor, nameof(ProjectResourceKind.Project), projectId, ProjectAction.Query, AccessRefusal.NotReadable);

            throw new BeaconNotFoundException($"Project {projectId} not found.");
        }

        throw AccessRefusal.Of(logger, actor, $"project ({ProjectAction.Query})", projectId, "Query is not allowed in this project.");
    }

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
