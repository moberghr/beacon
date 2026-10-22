using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Tools;
using Beacon.Core.Mcp;
using Beacon.Core.Services.Retention;

namespace Beacon.MCP.Services;

internal sealed class CrossSourceQueryService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IRowSecurityResolver rowSecurityResolver,
    IKnowledgeGraphService knowledgeGraph,
    ISqlGenerationService sqlGenerationService,
    ILlmRowDataEgress rowDataEgress,
    IOptions<McpDeploymentOptions> deploymentOptions,
    IMcpStatementLog statementLog,
    IQueryCostLimit costLimit,
    IOptions<QueryExecutionOptions> queryOptions,
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
        AskLlmBudget? llmBudget,
        CancellationToken ct)
    {
        // One generation per source and, when the results are executed, the join query: all of them must fit in the LLM
        // budget before the first is made; a repair is made only while they stay covered.
        var joinCalls = execute ? 1 : 0;
        var generationsLeft = sources.Count;
        if (llmBudget != null && llmBudget.Remaining < generationsLeft + joinCalls)
        {
            throw new AskLlmBudgetExceededException(llmBudget.MaxCalls);
        }

        var text = "";
        var failedSources = new List<(string Name, string Reason)>();

        var sourceQueries = new List<(RoutedSource Source, string Sql, SmartSchemaContext Context, ILlmProvider Provider)>();
        var columnsUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            // ask needs Query in the project, so live probes may run.
            var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(source.DataSourceId, projectId, question, liveValueProbes: true, ct);

            // Every prompt about this source carries its context: each request is logged with the row-derived data it carries.
            var sourceProvider = rowDataEgress.Track(
                llmProvider,
                new LlmRowDataScope(projectId, [source.DataSourceId], smartContext.RowDataCategories));

            llmBudget?.Consume();
            generationsLeft--;
            var sqlResult = await sqlGenerationService.GenerateAsync(
                sourceProvider, smartContext.FullContext, question, settings, ct);
            var sql = sqlResult.Sql;
            var generationRowSecurity = await rowSecurityResolver.ResolveAsync(source.DataSourceId, ct);

            // Pre-execution schema validation with one bounded repair, mirroring the single-source flow.
            // Only the SCHEMA verdict is acted on here: this loop also runs for execute=false (preview),
            // which never enforced read-only on SQL that is only rendered — the read-only gate runs in the
            // execution loop below, before anything reaches a provider.
            var schemaReport = gate.Evaluate(SqlGateRequest.FromSettings(sql, smartContext.DatabaseDialect, settings, generationRowSecurity) with
            {
                Catalog = smartContext.SchemaCatalog,
                ConfiguredDatabase = smartContext.ConfiguredDatabase
            });
            var schemaVerdict = schemaReport.Verdicts.Schema;
            if (schemaVerdict.Status == SqlGateStatus.Fail)
            {
                signal.SetSchemaValidationFailed(schemaVerdict.Message!);
                logger.LogInformation("Schema pre-validation failed for {DataSourceName}, retrying. Error: {Error}",
                    source.DataSourceName, schemaVerdict.Message);

                var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
                var retriedSql = llmBudget == null || llmBudget.TryConsume(keepInReserve: generationsLeft + joinCalls)
                    ? await sqlGenerationService.RetryWithErrorAsync(
                        sourceProvider, systemPrompt, sql, schemaVerdict.Message!, smartContext.FullContext, null, question, ct)
                    : null;

                var retryReport = retriedSql != null
                    ? gate.Evaluate(SqlGateRequest.FromSettings(retriedSql, smartContext.DatabaseDialect, settings, generationRowSecurity) with
                    {
                        Catalog = smartContext.SchemaCatalog,
                        ConfiguredDatabase = smartContext.ConfiguredDatabase
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

            sourceQueries.Add((source, sql, smartContext, sourceProvider));

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

        using var memDb = new InMemoryDatabaseManager(loggerFactory.CreateLogger<InMemoryDatabaseManager>())
        {
            MaxResultBytes = queryOptions.Value.MaxResultBytes
        };
        var anyExecuted = false;
        var customPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;
        // Each source's PII result columns (by name or by its SQL's aliasing), masked on the JOINED rows below where
        // the join query reads them back by name — never before the join, where the unanchored PII pattern would also
        // hit join keys (address_id, zip_code_id) and make distinct keys collide.
        var joinPiiColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sourceQueries.Count; i++)
        {
            var (source, sql, sourceContext, sourceProvider) = sourceQueries[i];

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var dataSource = await context.DataSources
                .Where(x => x.Id == source.DataSourceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {source.DataSourceId} not found");

            var dialect = DataSourceSqlDialect.Of(dataSource);
            var configuredDatabase = configuredDatabases.Resolve(dataSource);
            var rowSecurity = await rowSecurityResolver.ResolveAsync(dataSource.Id, ct);

            // Read-only gate (regex guardrail + AST, §1.5) BEFORE the provider dry-run — a write statement
            // must never reach EXPLAIN.
            var readOnlyReport = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings, rowSecurity) with
            {
                HostManagedKey = dataSource.HostManagedKey,
                ConfiguredDatabase = configuredDatabase
            });
            if (readOnlyReport.Blocked)
            {
                failedSources.Add((source.DataSourceName, readOnlyReport.BlockReason ?? "Validation failed"));
                text += $"**Validation Error for {source.DataSourceName}:** {readOnlyReport.BlockReason}\n";
                continue;
            }

            // Dry-run before execution: one bounded repair per source, never a hard block
            sql = await DryRunWithRepairAsync(sourceProvider, dataSource, configuredDatabase, rowSecurity, source, sql, sourceContext, question, settings, signal, llmBudget, joinCalls, ct);

            // The row cap is computed on the SQL that ACTUALLY runs (the repair above may have replaced it),
            // and honours the operator's MaxRowLimit instead of a hardcoded constant.
            var finalReport = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings, rowSecurity) with
            {
                MaxRows = settings.MaxRowLimit,
                HostManagedKey = dataSource.HostManagedKey,
                ConfiguredDatabase = configuredDatabase
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
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.StatementTimeoutSeconds));
            // §1.5 backstop — per-source execution goes through the read-only path. The database-level
            // guarantee is PostgreSQL and MySQL today (IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement);
            // other engines forward to normal execution and rely on the parser gates above.
            // MaxExplainCost (PostgreSQL only) is checked first, within the same timeout: a refusal fails this source with
            // the refusal as it is, and an estimate that failed is this source's execution error.
            string? costRefusal = null;
            var result = await statementLog.RecordAsync(
                dataSource.Id,
                McpStatementPurposes.SourceStep,
                limitedSql,
                async () =>
                {
                    var cost = await costLimit.CheckAsync(dataSource, limitedSql, new Dictionary<string, object?>(), settings.MaxExplainCost, timeoutCts.Token);
                    if (!cost.Passed)
                    {
                        costRefusal = cost.Refusal;

                        return new ProviderQueryResult { Success = false, ErrorMessage = cost.Refusal ?? cost.EstimateError };
                    }

                    return await provider.ExecuteReadOnlyQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);
                },
                x => (x.Success ? x.Rows.Count : null, x.Success ? null : x.ErrorMessage ?? McpContentRedactor.ClassExecution));

            // A provider reports a cancelled statement as a failed result: the call's own cancellation (the caller's, or
            // the ask's time budget) ends the call instead of reading as this source's execution error.
            ct.ThrowIfCancellationRequested();

            if (costRefusal != null)
            {
                failedSources.Add((source.DataSourceName, costRefusal));
                text += $"**Refused for {source.DataSourceName}:** {costRefusal}\n";
                continue;
            }

            if (!result.Success)
            {
                // The database's error can quote row values: the caller reads it redacted while PII detection is on (a host
                // data source's error is already Beacon's generic text).
                var error = DatabaseErrorRedactor.ForCaller(
                    result.ErrorMessage ?? "Execution failed",
                    limitedSql,
                    settings.EnablePiiDetection && dataSource.HostManagedKey == null,
                    logger);
                failedSources.Add((source.DataSourceName, error));
                text += $"**Execution Error for {source.DataSourceName}:** {error}\n";
                continue;
            }

            anyExecuted = true;

            if (result.Rows?.Count > 0)
            {
                // Before rows enter the in-memory join store, mask only the SQL-text PII matches and host-masked
                // columns (§1.6/§1.11) — finalReport was evaluated on the SQL that ACTUALLY executed, after
                // DryRunWithRepairAsync may have replaced `sql`. Result-column and alias masking happens on the
                // joined rows, so it cannot corrupt a join key.
                var rows = PiiRowMasker.Mask(guardrailService, result.Rows, finalReport.PiiColumns, detectByColumnName: false, customPatterns);
                if (settings.EnablePiiDetection)
                {
                    joinPiiColumns.UnionWith(PiiRowMasker.ResolvePiiColumns(
                        guardrailService,
                        result.Rows.SelectMany(x => x.Keys),
                        finalReport.PiiColumns,
                        detectByColumnName: true,
                        customPatterns,
                        limitedSql,
                        dialect));
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
            MaxTokens = 1024,
            Operation = LlmOperations.AskCrossSourceJoin
        };

        llmBudget?.Consume();
        var joinResponse = await llmProvider.CompleteAsync(joinRequest, ct);
        var joinSql = SqlParsingHelper.CleanSqlResponse(joinResponse.Content);
        var translatedSql = memDb.TranslateFinalQuery(joinSql);

        text += $"\n### Join Query\n```sql\n{translatedSql}\n```\n\n";

        // The join SQL is LLM-generated from the user's question — run the same read-only gate (regex
        // guardrail + AST, §1.5) applied to each per-source query above before it touches the in-memory
        // SQLite store, so the final join is not a gap.
        // At most MaxRowLimit rows are returned: one row past it is written into the SQL and read back, so a result above
        // the limit is reported as truncated.
        var joinRowCap = Math.Max(1, settings.MaxRowLimit);
        var joinReport = gate.Evaluate(SqlGateRequest.ForInMemoryJoin(translatedSql, settings) with { MaxRows = joinRowCap + 1 });
        if (joinReport.Blocked)
        {
            text += $"**Validation Error for join query:** {joinReport.BlockReason}\n";

            return (text, false);
        }

        List<IDictionary<string, object?>> joinResults;
        double execTimeMs;
        bool timedOut;
        try
        {
            (joinResults, execTimeMs, timedOut) = await memDb.ExecuteQueryAsync(joinReport.FinalSql, settings.StatementTimeoutSeconds, joinRowCap + 1, ct);
        }
        catch (ResultTooLargeException ex)
        {
            // The message names the byte limit only.
            text += $"**Error:** {ex.Message}\n";

            return (text, false);
        }

        if (timedOut)
        {
            text += "**Error:** Join query timed out.\n";

            return (text, false);
        }

        if (joinResults.Count > 0)
        {
            // The joined output returned to the MCP client gets the full masking: the sources' PII columns read
            // back by name, the join SQL's text matches, PII-named result columns and the join SQL's aliases.
            var joinedRows = PiiRowMasker.Mask(
                guardrailService,
                joinResults
                    .Take(joinRowCap)
                    .Select(x => new Dictionary<string, object?>(x))
                    .ToList(),
                joinPiiColumns.Concat(joinReport.PiiColumns),
                settings.EnablePiiDetection,
                customPatterns,
                translatedSql,
                "SQLite");

            text += $"### Final Results ({joinedRows.Count} rows, {execTimeMs:F0}ms)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(joinedRows, maxResultBytes: settings.MaxResultBytes);
            if (joinResults.Count > joinRowCap)
            {
                text += $"\n_The join returned more than the row limit of {joinRowCap}; only the first {joinRowCap} rows were kept. Narrow the question to see the rest._\n";
            }
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
        string? configuredDatabase,
        RowSecurityContext rowSecurity,
        RoutedSource source,
        string sql,
        SmartSchemaContext sourceContext,
        string question,
        McpSettingsData settings,
        McpSignalBuilder signal,
        AskLlmBudget? llmBudget,
        int joinCalls,
        CancellationToken ct)
    {
        string? dryRunError;
        try
        {
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            var dryRun = await DryRunAsync(provider, dataSource, sql, ct);

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
            // Dry-run infrastructure failures never block the cross-source flow. The type and error code only (§1.11).
            logger.LogWarning(
                "Dry-run validation unavailable for data source {DataSourceId}: {Failure}",
                dataSource.Id,
                FailureCodes.Describe(ex));
            return sql;
        }

        // The repair prompt and the stored signal get the error without the values it quotes — the prompt only its class and
        // code while row data may not reach the LLM provider; the log its class only.
        var redactedError = DatabaseErrorRedactor.Redact(dryRunError, DatabaseErrorRedactor.KnownNames(sql, sourceContext.SchemaCatalog), logger);
        var classOnly = DatabaseErrorRedactor.ClassOnly(dryRunError, logger);
        logger.LogInformation(
            "Dry-run failed for data source {DataSourceId}, retrying ({ErrorClass})",
            dataSource.Id,
            classOnly);
        signal.SetDryRunFailed(redactedError);

        // Every generation is done by now, so the repair keeps only the join query's call covered; without room it is skipped
        // and the statement runs as generated.
        if (llmBudget != null && !llmBudget.TryConsume(keepInReserve: joinCalls))
        {
            logger.LogInformation("LLM call budget spent; dry-run repair skipped for data source {DataSourceId}", dataSource.Id);

            return sql;
        }

        var promptError = deploymentOptions.Value.ForbidRowDataToLlm ? classOnly : redactedError;
        var systemPrompt = settings.AskSystemPrompt ?? "You are a SQL expert. Return ONLY the SQL query.";
        var retriedSql = await sqlGenerationService.RetryWithErrorAsync(
            llmProvider, systemPrompt, sql, promptError, sourceContext.FullContext, null, question, ct);

        if (retriedSql == null)
        {
            return sql;
        }

        // The retry must clear the same read-only gate the original did before it can be adopted.
        if (gate.Evaluate(SqlGateRequest.FromSettings(retriedSql, DataSourceSqlDialect.Of(dataSource), settings, rowSecurity) with { ConfiguredDatabase = configuredDatabase }).Blocked)
        {
            return sql;
        }

        // Re-run the dry-run to confirm the repair actually fixed the query before adopting it
        try
        {
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            var reDryRun = await DryRunAsync(provider, dataSource, retriedSql, ct);
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
            logger.LogWarning(
                "Re-validation unavailable for data source {DataSourceId}: {Failure}",
                dataSource.Id,
                FailureCodes.Describe(ex));
        }

        signal.SetRetry(retriedSql, true);

        return retriedSql;
    }

    // A dry run reaches the data source too: on the call's audit row as a dry-run statement.
    private Task<Core.Models.Providers.QueryValidationResult> DryRunAsync(IDataSourceProvider provider, Core.Data.Entities.DataSource dataSource, string sql, CancellationToken ct)
    {
        return statementLog.RecordAsync(
            dataSource.Id,
            McpStatementPurposes.DryRun,
            sql,
            () => provider.ValidateQueryAsync(dataSource, sql, ct),
            x => (null, x.IsValid || x.Skipped ? null : McpContentRedactor.ClassValidation));
    }
}
