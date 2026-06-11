using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

internal sealed class CrossSourceQueryService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    IKnowledgeGraphService knowledgeGraph,
    ISqlGenerationService sqlGenerationService,
    ILoggerFactory loggerFactory,
    ILogger<CrossSourceQueryService> logger) : ICrossSourceQueryService
{
    public async Task<string> ExecuteAsync(
        ILlmProvider llmProvider,
        List<RoutedSource> sources,
        string question,
        McpSettingsData settings,
        bool execute,
        CancellationToken ct)
    {
        var text = "";

        var sourceQueries = new List<(RoutedSource Source, string Sql, string SchemaContext)>();
        foreach (var source in sources)
        {
            var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(source.DataSourceId, question, ct);

            var sqlResult = await sqlGenerationService.GenerateAsync(
                llmProvider, smartContext.FullContext, question, settings, ct);

            sourceQueries.Add((source, sqlResult.Sql, smartContext.FullContext));

            text += $"### Source: {source.DataSourceName}\n```sql\n{sqlResult.Sql}\n```\n\n";
        }

        if (!execute)
        {
            return text + "*Execution skipped (execute=false)*\n";
        }

        using var memDb = new InMemoryDatabaseManager(loggerFactory.CreateLogger<InMemoryDatabaseManager>());

        for (var i = 0; i < sourceQueries.Count; i++)
        {
            var (source, sql, schemaContext) = sourceQueries[i];
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
            var dataSource = await context.DataSources
                .Where(x => x.Id == source.DataSourceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {source.DataSourceId} not found");

            // AST-based read-only defense-in-depth on top of the regex guardrail (§1.5)
            if (settings.EnforceReadOnly)
            {
                var astError = readOnlyAstValidator.Validate(sql, dataSource.DatabaseEngineType?.ToString());
                if (astError != null)
                {
                    text += $"**Validation Error for {source.DataSourceName}:** {astError}\n";

                    return text;
                }
            }

            // Dry-run before execution: one bounded repair per source, never a hard block
            sql = await DryRunWithRepairAsync(llmProvider, dataSource, source, sql, schemaContext, question, settings, ct);

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

        var analysis = memDb.AnalyzeDatabase();
        var tablesInfo = string.Join("\n", analysis.Tables.Values.Select(x =>
            $"Table: {x.TableName} ({x.RowCount} rows, {x.ColumnCount} columns, from {x.SourceProject})"));

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
        var joinSql = SqlParsingHelper.CleanSqlResponse(joinResponse.Content);
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

    private async Task<string> DryRunWithRepairAsync(
        ILlmProvider llmProvider,
        Core.Data.Entities.DataSource dataSource,
        RoutedSource source,
        string sql,
        string schemaContext,
        string question,
        McpSettingsData settings,
        CancellationToken ct)
    {
        string? dryRunError;
        try
        {
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            var dryRun = await provider.ValidateQueryAsync(dataSource, sql, ct);
            if (dryRun.IsValid)
            {
                return sql;
            }

            dryRunError = dryRun.Errors is { Count: > 0 }
                ? string.Join("; ", dryRun.Errors)
                : "Query validation failed.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Dry-run infrastructure failures never block the cross-source flow
            logger.LogWarning(ex, "Dry-run validation unavailable for data source {DataSourceId}", dataSource.Id);
            return sql;
        }

        logger.LogInformation("Dry-run failed for {DataSourceName}, retrying. Error: {Error}", source.DataSourceName, dryRunError);

        var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
        var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
            llmProvider, systemPrompt, sql, dryRunError, schemaContext, null, question, ct);

        if (retriedSql == null)
        {
            return sql;
        }

        var retryValidation = guardrailService.ValidateQuery(retriedSql, new QueryGuardrailOptions
        {
            ReadOnly = settings.EnforceReadOnly,
            DetectPii = settings.EnablePiiDetection,
            CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
        });
        if (!retryValidation.IsValid)
        {
            return sql;
        }

        if (settings.EnforceReadOnly && readOnlyAstValidator.Validate(retriedSql, dataSource.DatabaseEngineType?.ToString()) != null)
        {
            return sql;
        }

        return retriedSql;
    }
}
