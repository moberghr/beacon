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
using Beacon.Core.Services.Validation;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

internal sealed class CrossSourceQueryService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    ISqlExecutionGate gate,
    IKnowledgeGraphService knowledgeGraph,
    ISqlGenerationService sqlGenerationService,
    ILoggerFactory loggerFactory,
    ILogger<CrossSourceQueryService> logger) : ICrossSourceQueryService
{
    public async Task<(string Text, bool Succeeded)> ExecuteAsync(
        ILlmProvider llmProvider,
        List<RoutedSource> sources,
        int projectId,
        string question,
        McpSettingsData settings,
        bool execute,
        McpSignalBuilder signal,
        CancellationToken ct)
    {
        var text = "";
        var failedSources = new List<(string Name, string Reason)>();

        var sourceQueries = new List<(RoutedSource Source, string Sql, string SchemaContext)>();
        var columnsUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(source.DataSourceId, projectId, question, ct);

            var sqlResult = await sqlGenerationService.GenerateAsync(
                llmProvider, smartContext.FullContext, question, settings, ct);
            var sql = sqlResult.Sql;

            // Pre-execution schema validation with one bounded repair, mirroring the single-source flow.
            // Only the SCHEMA verdict is acted on here: this loop also runs for execute=false (preview),
            // which never enforced read-only on SQL that is only rendered — the read-only gate runs in the
            // execution loop below, before anything reaches a provider.
            var schemaReport = gate.Evaluate(SqlGateRequest.FromSettings(sql, smartContext.DatabaseDialect, settings) with
            {
                Catalog = smartContext.SchemaCatalog
            });
            var schemaVerdict = schemaReport.Verdicts.Schema;
            if (schemaVerdict.Status == SqlGateStatus.Fail)
            {
                signal.SetSchemaValidationFailed(schemaVerdict.Message!);
                logger.LogInformation("Schema pre-validation failed for {DataSourceName}, retrying. Error: {Error}",
                    source.DataSourceName, schemaVerdict.Message);

                var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
                var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
                    llmProvider, systemPrompt, sql, schemaVerdict.Message!, smartContext.FullContext, null, question, ct);

                var retryReport = retriedSql != null
                    ? gate.Evaluate(SqlGateRequest.FromSettings(retriedSql, smartContext.DatabaseDialect, settings) with
                    {
                        Catalog = smartContext.SchemaCatalog
                    })
                    : null;
                var retryValid = retryReport?.Verdicts.Schema.Status is SqlGateStatus.Pass or SqlGateStatus.Skipped;
                if (retriedSql != null)
                {
                    signal.SetRetry(retriedSql, retryValid);
                }

                if (!retryValid)
                {
                    failedSources.Add((source.DataSourceName, $"Schema validation failed: {schemaVerdict.Message}"));
                    text += $"### Source: {source.DataSourceName}\n**Schema Validation Error:** {schemaVerdict.Message}\n\n";
                    continue;
                }

                sql = retriedSql!;
                columnsUsed.UnionWith(retryReport!.ColumnsUsed);
            }
            else
            {
                columnsUsed.UnionWith(schemaReport.ColumnsUsed);
            }

            sourceQueries.Add((source, sql, smartContext.FullContext));

            text += $"### Source: {source.DataSourceName}\n```sql\n{sql}\n```\n\n";
        }

        signal.SetColumnsUsed(columnsUsed.ToList());

        if (!execute)
        {
            text += FormatFailedSourcesNote(failedSources);

            return (text + "*Execution skipped (execute=false)*\n", sourceQueries.Count > 0);
        }

        if (sourceQueries.Count == 0)
        {
            return (text + FormatFailedSourcesNote(failedSources), false);
        }

        using var memDb = new InMemoryDatabaseManager(loggerFactory.CreateLogger<InMemoryDatabaseManager>());
        var anyExecuted = false;

        for (var i = 0; i < sourceQueries.Count; i++)
        {
            var (source, sql, schemaContext) = sourceQueries[i];

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var dataSource = await context.DataSources
                .Where(x => x.Id == source.DataSourceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {source.DataSourceId} not found");

            var dialect = dataSource.DatabaseEngineType?.ToString();

            // Read-only gate (regex guardrail + AST, §1.5) BEFORE the provider dry-run — a write statement
            // must never reach EXPLAIN.
            var readOnlyReport = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings));
            if (readOnlyReport.Blocked)
            {
                failedSources.Add((source.DataSourceName, readOnlyReport.BlockReason ?? "Validation failed"));
                text += $"**Validation Error for {source.DataSourceName}:** {readOnlyReport.BlockReason}\n";
                continue;
            }

            // Dry-run before execution: one bounded repair per source, never a hard block
            sql = await DryRunWithRepairAsync(llmProvider, dataSource, source, sql, schemaContext, question, settings, signal, ct);

            // The row cap is computed on the SQL that ACTUALLY runs (the repair above may have replaced it),
            // and honours the operator's MaxRowLimit instead of a hardcoded constant.
            var finalReport = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings) with
            {
                MaxRows = settings.MaxRowLimit
            });
            if (finalReport.Blocked)
            {
                failedSources.Add((source.DataSourceName, finalReport.BlockReason ?? "Validation failed"));
                text += $"**Validation Error for {source.DataSourceName}:** {finalReport.BlockReason}\n";
                continue;
            }

            var limitedSql = finalReport.FinalSql;
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            // §1.5 backstop — per-source execution goes through the read-only path. The database-level
            // guarantee is PostgreSQL-only today (IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement);
            // other engines forward to normal execution and rely on the parser gates above.
            var result = await provider.ExecuteReadOnlyQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
            {
                failedSources.Add((source.DataSourceName, result.ErrorMessage ?? "Execution failed"));
                text += $"**Execution Error for {source.DataSourceName}:** {result.ErrorMessage}\n";
                continue;
            }

            anyExecuted = true;

            if (result.Rows?.Count > 0)
            {
                // Mask PII before rows enter the in-memory join store so the joined output returned to the
                // MCP client is masked too (§1.6/§1.11). Recompute PII columns from the SQL that ACTUALLY
                // executed — DryRunWithRepairAsync above may have replaced `sql`, so the pre-repair
                // `validation.PiiColumns` can reference the wrong columns.
                var rows = result.Rows;
                if (settings.EnablePiiDetection)
                {
                    var piiColumns = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                    {
                        ReadOnly = false,
                        DetectPii = true,
                        CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                    }).PiiColumns;

                    if (piiColumns is { Count: > 0 } piiCols)
                    {
                        rows = rows.Select(x => guardrailService.MaskPiiValues(x, piiCols)).ToList();
                    }
                }

                var tableName = $"result{i + 1}";
                await memDb.CreateTableFromResults(tableName, rows.Cast<IDictionary<string, object?>>().ToList(), new ProjectInfo
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

        if (!anyExecuted)
        {
            return (text + FormatFailedSourcesNote(failedSources), false);
        }

        text += FormatFailedSourcesNote(failedSources);

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

        // The join SQL is LLM-generated from the user's question — run the same read-only gate (regex
        // guardrail + AST, §1.5) applied to each per-source query above before it touches the in-memory
        // SQLite store, so the final join is not a gap.
        var joinReport = gate.Evaluate(SqlGateRequest.FromSettings(translatedSql, "SQLite", settings));
        if (joinReport.Blocked)
        {
            text += $"**Validation Error for join query:** {joinReport.BlockReason}\n";

            return (text, false);
        }

        var (joinResults, execTimeMs, timedOut) = await memDb.ExecuteQueryAsync(translatedSql, 30);

        if (timedOut)
        {
            text += "**Error:** Join query timed out.\n";

            return (text, false);
        }

        if (joinResults.Count > 0)
        {
            text += $"### Final Results ({joinResults.Count} rows, {execTimeMs:F0}ms)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(joinResults);
        }
        else
        {
            text += "No results from join query.\n";
        }

        return (text, true);
    }

    private static string FormatFailedSourcesNote(List<(string Name, string Reason)> failedSources)
    {
        if (failedSources.Count == 0)
        {
            return "";
        }

        var note = "\n**Note:** The following sources failed and were excluded from the cross-source result:\n";
        foreach (var (name, reason) in failedSources)
        {
            note += $"- {name}: {reason}\n";
        }

        return note + "\n";
    }

    private async Task<string> DryRunWithRepairAsync(
        ILlmProvider llmProvider,
        Core.Data.Entities.DataSource dataSource,
        RoutedSource source,
        string sql,
        string schemaContext,
        string question,
        McpSettingsData settings,
        McpSignalBuilder signal,
        CancellationToken ct)
    {
        string? dryRunError;
        try
        {
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            var dryRun = await provider.ValidateQueryAsync(dataSource, sql, ct);

            // Skipped = the engine has no dry-run strategy — nothing was checked, so there is nothing
            // to repair; proceed exactly like the infrastructure-failure path below.
            if (dryRun.IsValid || dryRun.Skipped)
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
        signal.SetDryRunFailed(dryRunError);

        var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
        var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
            llmProvider, systemPrompt, sql, dryRunError, schemaContext, null, question, ct);

        if (retriedSql == null)
        {
            return sql;
        }

        // The retry must clear the same read-only gate the original did before it can be adopted.
        if (gate.Evaluate(SqlGateRequest.FromSettings(retriedSql, dataSource.DatabaseEngineType?.ToString(), settings)).Blocked)
        {
            return sql;
        }

        // Re-run the dry-run to confirm the repair actually fixed the query before adopting it
        try
        {
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            var reDryRun = await provider.ValidateQueryAsync(dataSource, retriedSql, ct);
            if (!reDryRun.IsValid && !reDryRun.Skipped)
            {
                signal.SetRetry(retriedSql, false);

                return sql;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Re-validation unavailable for data source {DataSourceId}", dataSource.Id);
        }

        signal.SetRetry(retriedSql, true);

        return retriedSql;
    }
}
