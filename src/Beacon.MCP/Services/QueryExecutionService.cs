using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.HostData;
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

internal sealed class QueryExecutionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    ISharedSourceSettings sharedSourceSettings,
    IProjectContext projectContext,
    IMcpStatementLog statementLog,
    IQueryCostLimit costLimit,
    IHostDataSourceGuard hostGuard,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IRowSecurityResolver rowSecurityResolver) : IQueryExecutionService
{
    public async Task<QueryExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        // The tool resolved the project before delegating here (ToolHelper.ResolveProjectId sets ActiveProjectId); 0 falls
        // back to the global effective settings — with the strictest guardrails of the live projects that link the source.
        // Resolved before execution: the statement timeout and the result size budget come from them.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectContext.ActiveProjectId ?? 0, [dataSourceId], ct);

        // The statement runs as the gate writes it (§1.5): the row cap is the gate's own row-limit stage, so what reaches
        // the source is the gate's FinalSql, never SQL changed after the gate. The pipeline judged the same SQL while it
        // generated and repaired it; this evaluation, under the source's effective settings, decides what runs, and a
        // refusal reaches no source.
        var rowSecurity = await rowSecurityResolver.ResolveAsync(dataSourceId, ct);
        var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, DataSourceSqlDialect.Of(dataSource), settings, rowSecurity) with
        {
            MaxRows = maxRows,
            HostManagedKey = dataSource.HostManagedKey,
            ConfiguredDatabase = configuredDatabases.Resolve(dataSource)
        });
        if (report.Blocked)
        {
            return new QueryExecutionResult(null, report.BlockReason ?? "The SQL was rejected.", 0, false, Refused: true);
        }

        var limitedSql = report.FinalSql;
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.StatementTimeoutSeconds));
        // §1.5 backstop — read-only execution path. The database-level guarantee is PostgreSQL and MySQL
        // today (IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement); other engines and
        // non-database providers forward to normal execution and rely on the parser gates upstream.
        // On the call's audit row as it reached the source, under the purpose the caller runs it for (§9.5, failures too).
        // MaxExplainCost (PostgreSQL only) is checked first, within the same timeout: a refusal is recorded on the audit
        // row as the statement's error and returned to the caller as it is, and an estimate that failed is the
        // statement's failure (redacted below like any database error).
        string? costRefusal = null;
        var result = await statementLog.RecordAsync(
            dataSourceId,
            null,
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

        if (costRefusal != null)
        {
            return new QueryExecutionResult(null, costRefusal, 0, false, Refused: true);
        }

        if (result.Success && result.Rows?.Count > 0)
        {
            // Mask PII column values before returning to the MCP client (§1.6/§1.11). Read-only was
            // already enforced upstream, so here we only need PII detection.
            var customPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;
            var sqlPiiColumns = settings.EnablePiiDetection
                ? guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                {
                    ReadOnly = false,
                    DetectPii = true,
                    CustomPiiPatterns = customPatterns,
                    Dialect = DataSourceSqlDialect.Of(dataSource)
                }).PiiColumns ?? []
                : [];

            // The output columns the host policy masks for this statement, as the gate's report carries them on the query
            // tool path: masked in full whatever the detection setting, on top of the provider's own positional masking. A
            // check that refuses names no masked column, so every result column is masked in full (fail closed); its
            // error is not logged, as it can quote the SQL (§1.11).
            var hostCheck = hostGuard.Check(dataSource, limitedSql);
            var hostMaskedColumns = hostCheck.Allowed
                ? hostCheck.MaskedOutputColumns
                : result.Rows
                    .SelectMany(x => x.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            // The executed SQL resolves the names it aliases PII columns to (email AS contact).
            var rows = PiiRowMasker.Mask(
                guardrailService,
                result.Rows,
                sqlPiiColumns,
                settings.EnablePiiDetection,
                customPatterns,
                limitedSql,
                DataSourceSqlDialect.Of(dataSource),
                hostMaskedColumns);

            // The text and the structured payload together stay within the resolved MaxResultBytes.
            var (text, structured) = ToolHelper.FormatResultWithinBudget($"### Results ({rows.Count} rows)\n\n", rows, maxRows, settings.MaxResultBytes);

            return new QueryExecutionResult(text, null, rows.Count, true, structured);
        }

        if (!result.Success)
        {
            // A database error can quote row values: while PII detection is on the caller gets it redacted (a host data
            // source's error is already Beacon's own generic text).
            var error = result.ErrorMessage == null
                ? null
                : DatabaseErrorRedactor.ForCaller(result.ErrorMessage, sql, settings.EnablePiiDetection && dataSource.HostManagedKey == null);

            return new QueryExecutionResult(null, error, 0, false);
        }

        return new QueryExecutionResult("No results returned.\n", null, 0, true);
    }

    public async Task<ProviderDryRunOutcome> ValidateAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        var result = await statementLog.RecordAsync(
            dataSourceId,
            McpStatementPurposes.DryRun,
            sql,
            () => provider.ValidateQueryAsync(dataSource, sql, ct),
            x => (null, x.IsValid || x.Skipped ? null : McpContentRedactor.ClassValidation));

        if (result.IsValid)
        {
            return ProviderDryRunOutcome.Valid();
        }

        var message = result.Errors is { Count: > 0 }
            ? string.Join("; ", result.Errors)
            : "Query validation failed.";

        // Skipped = the engine has no dry-run strategy; the message carries the skip reason so callers
        // can surface it, but it is NOT a validation failure.
        if (result.Skipped || dataSource.HostManagedKey != null)
        {
            return new ProviderDryRunOutcome(message, result.Skipped);
        }

        // A dry-run failure is the database's own error, which can quote row values: redacted while PII detection is on.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectContext.ActiveProjectId ?? 0, [dataSourceId], ct);

        return new ProviderDryRunOutcome(DatabaseErrorRedactor.ForCaller(message, sql, settings.EnablePiiDetection), false);
    }
}
