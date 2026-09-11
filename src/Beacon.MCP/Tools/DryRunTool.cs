using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class DryRunTool(
    IDbContextFactory<BeaconContext> contextFactory,
    ISqlExecutionGate gate,
    IKnowledgeGraphService knowledgeGraph,
    IQueryExecutionService queryExecutionService,
    IMcpSettingsProvider settingsProvider,
    IProjectContext projectContext,
    McpAuditService auditService,
    McpSignalService signalService,
    ILogger<DryRunTool> logger)
{
    // The dry-run preview applies the same default row budget the query tool uses, capped by settings.
    private const int DefaultMaxRows = 100;

    private const string ReadOnlyGate = "read_only";
    private const string SchemaGate = "schema";
    private const string ProviderGate = "provider_dry_run";

    [McpServerTool(Name = "dry_run", Title = "Validate SQL Without Executing", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Validate a SQL query through all of Beacon's safety gates — the read-only gate (regex guardrail + AST validation), schema column check, and a provider dry-run (EXPLAIN) — without executing it. Returns the exact SQL that would run (with the row limit applied) and any issues found. If the data source has no extracted schema metadata yet, the schema gate reports an advisory issue and the verdict is invalid (the column check could not be performed); the provider dry-run still runs. Engines without a provider dry-run strategy (e.g. SQLite) report that gate as skipped with an advisory issue, so a query that could not be validated is never reported as valid. Use before query.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("Name of the data source to validate against (preferred)")]
        string? datasource_name = null,
        [Description("ID of the data source to validate against (alternative to name)")]
        int? datasource_id = null,
        [Description("The SQL query to validate (SELECT only)")]
        string? sql = null,
        [Description("Optional. Specify project if your API key has access to multiple projects.")]
        int? project_id = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // dry_run IS part of the SQL-learning loop (unlike the read tools — see GetContextTool): the
        // caller-authored SQL passing or failing the gates is exactly the outcome McpQuerySignal models
        // (Question = GeneratedSql = the SQL under validation). §9.5 — recorded on every path, failures
        // included (codex PR-11 R4).
        var signal = new McpSignalBuilder()
            .SetTool("dry_run")
            .SetQuestion(sql ?? "")
            .SetUserId(projectContext.UserId);

        if (string.IsNullOrEmpty(sql))
        {
            return await FailAsync(signal, sw, null, datasource_id, sql, "Missing required parameter: sql", cancellationToken);
        }

        // The data source (and therefore the dialect) is not resolved yet, so the early-exit failures below
        // carry the SQL without tables; the gate's AST-resolved tables are recorded once it has run.
        signal.SetGeneratedSql(sql);

        var resolveError = ToolHelper.ResolveProjectId(projectContext, project_id, out var projectId);
        if (resolveError != null)
        {
            return await FailAsync(signal, sw, null, datasource_id, sql, resolveError, cancellationToken);
        }

        signal.SetProjectId(projectId);

        if (datasource_id == null && string.IsNullOrEmpty(datasource_name))
        {
            return await FailAsync(signal, sw, projectId, null, sql, "Provide either datasource_name or datasource_id.", cancellationToken);
        }

        if (datasource_id == null && !string.IsNullOrEmpty(datasource_name))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, datasource_name, cancellationToken);
            if (nameError != null)
            {
                return await FailAsync(signal, sw, projectId, null, sql, nameError, cancellationToken);
            }

            datasource_id = resolvedId;
        }

        signal.SetDataSourceId(datasource_id);

        var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, datasource_id!.Value, cancellationToken);
        if (projectError != null)
        {
            return await FailAsync(signal, sw, projectId, datasource_id, sql, projectError, cancellationToken);
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var dataSource = await context.DataSources
                .Where(x => x.Id == datasource_id.Value)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Data source {datasource_id} not found");

            if (dataSource.DataSourceType == DataSourceType.Api)
            {
                return await FailAsync(signal, sw, projectId, datasource_id, sql,
                    "dry_run validates SQL only — API data sources are not supported.", cancellationToken);
            }

            var dialect = dataSource.DatabaseEngineType?.ToString();
            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, cancellationToken);
            var catalog = await knowledgeGraph.GetSchemaCatalogAsync(datasource_id.Value, cancellationToken);
            var maxRows = Math.Min(DefaultMaxRows, settings.MaxRowLimit);

            // Gates 1-3 (read-only, schema, row limit) run through the one shared gate (§1.5). The schema
            // verdict is advisory here — every issue is collected so the caller sees all of them at once.
            var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings) with
            {
                Catalog = catalog,
                MaxRows = maxRows
            });
            signal.SetGeneratedSql(sql, report.TablesUsed.ToList());

            var issues = new List<GateIssue>();

            var readOnly = report.Verdicts.ReadOnly;
            var readOnlyGateRan = readOnly.Status != SqlGateStatus.Skipped;
            if (readOnly.Status == SqlGateStatus.Fail)
            {
                issues.Add(new GateIssue(ReadOnlyGate, readOnly.Code ?? SqlGateCodes.Guardrail, readOnly.Message ?? "Query validation failed"));
            }

            var piiColumns = report.PiiColumns;

            // An EMPTY catalog means the check cannot run at all — surface that as an advisory issue
            // (making the verdict invalid: the caller asked for a validation that could not be
            // performed) instead of rendering a vacuous "✓ schema".
            var schema = report.Verdicts.Schema;
            var schemaCheckSkipped = schema.Code == SqlGateCodes.EmptyCatalog;
            var schemaNotEvaluated = schema.Code == SqlGateCodes.NotEvaluated;
            if (schemaCheckSkipped)
            {
                issues.Add(new GateIssue(SchemaGate, SqlGateCodes.EmptyCatalog,
                    "No schema metadata available for this data source yet — column check was skipped. Run metadata extraction, or verify column names manually."));
            }
            else if (schema.Status == SqlGateStatus.Fail)
            {
                issues.Add(new GateIssue(SchemaGate, SqlGateCodes.Schema, schema.Message ?? "Schema validation failed"));
            }

            // Gate 4: provider dry-run (EXPLAIN / sp_describe_first_result_set) — ONLY when every
            // previous gate passed; running EXPLAIN on a known-write statement is pointless and unsafe.
            // The schema-skip advisory alone does not block it: the parsers passed, so the provider
            // dry-run is the only column check still available. An engine WITHOUT a dry-run strategy
            // reports Skipped — surfaced as an advisory issue mirroring the empty-catalog pattern
            // (verdict invalid: the caller asked for a validation that could not be performed).
            var providerGateRan = false;
            var providerGateSkipped = false;
            if (issues.Count == 0 || (schemaCheckSkipped && issues.Count == 1))
            {
                providerGateRan = true;
                var providerOutcome = await queryExecutionService.ValidateAsync(datasource_id.Value, sql, cancellationToken);
                if (providerOutcome.Skipped)
                {
                    providerGateSkipped = true;
                    issues.Add(new GateIssue(ProviderGate, "skipped",
                        providerOutcome.Error ?? "Provider dry-run is not supported for this engine — validation was skipped. Verify the query manually."));
                }
                else if (providerOutcome.Error != null)
                {
                    issues.Add(new GateIssue(ProviderGate, "provider", providerOutcome.Error));
                }
            }

            var valid = issues.Count == 0;
            var executableSql = valid ? report.FinalSql : null;

            var text = BuildMarkdown(valid, issues, readOnlyGateRan, schemaNotEvaluated, providerGateRan, providerGateSkipped, executableSql, piiColumns);
            var structured = BuildStructuredContent(valid, issues, executableSql, piiColumns);

            // Signal fields mirror the ask flow's failure taxonomy: schema-gate issues → schema
            // validation failure, provider gate → dry-run failure, read-only gate → execution
            // validation failure. IsSuccessful is the verdict itself.
            RecordGateFailuresOnSignal(signal, issues, providerGateSkipped);
            sw.Stop();
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, valid);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "dry_run",
                sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
            return ToolHelper.Success(text, structured);
        }
        catch (Exception ex)
        {
            sw.Stop();
            signal.SetExecutionFailed(ex.Message);
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "dry_run",
                sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            await signalService.RecordSignalAsync(signal.Build(), CancellationToken.None);
            // §1.11 — ex.Message can quote the user's SQL; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "dry_run", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "dry_run"));
        }
    }

    // §1.7/§9.5 — audit AND signal must be recorded on every outcome, including the early-exit
    // failures before the gate pipeline (missing input, project/data-source resolution, access denied).
    private async Task<CallToolResult> FailAsync(
        McpSignalBuilder signal,
        Stopwatch sw,
        int? projectId,
        int? dataSourceId,
        string? sql,
        string error,
        CancellationToken cancellationToken)
    {
        sw.Stop();
        signal.SetExecutionFailed(error);
        signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
        await auditService.LogToolCallAsync(null, projectContext.UserId, "dry_run",
            sql, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, error, cancellationToken);
        await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
        return ToolHelper.Error(error);
    }

    private static string BuildMarkdown(
        bool valid,
        IReadOnlyList<GateIssue> issues,
        bool readOnlyGateRan,
        bool schemaNotEvaluated,
        bool providerGateRan,
        bool providerGateSkipped,
        string? executableSql,
        IReadOnlyList<string> piiColumns)
    {
        var text = valid
            ? "# Dry Run\n\n**VALID** — all safety gates passed. The query was NOT executed.\n\n"
            : $"# Dry Run\n\n**INVALID** — {issues.Count} issue(s) found. The query was NOT executed.\n\n";

        text += readOnlyGateRan
            ? GateLine(ReadOnlyGate, issues)
            : $"- – {ReadOnlyGate} — skipped (read-only enforcement disabled)\n";
        text += schemaNotEvaluated
            ? $"- – {SchemaGate} — not evaluated (read-only gate failed)\n"
            : GateLine(SchemaGate, issues);
        text += ProviderGateLine(providerGateRan, providerGateSkipped, issues);

        if (executableSql != null)
        {
            text += $"\n### SQL that would execute\n```sql\n{executableSql}\n```\n";
        }

        if (piiColumns.Count > 0)
        {
            text += $"\n**PII columns that would be masked:** {string.Join(", ", piiColumns)}\n";
        }

        return text;
    }

    private static string ProviderGateLine(bool ran, bool skipped, IReadOnlyList<GateIssue> issues)
    {
        if (skipped)
        {
            return $"- – {ProviderGate} — not supported for this engine (skipped)\n";
        }

        return ran
            ? GateLine(ProviderGate, issues)
            : $"- – {ProviderGate} — skipped (fix the issues above first)\n";
    }

    private static string GateLine(string gate, IReadOnlyList<GateIssue> issues)
    {
        var issue = issues
            .Where(x => x.Gate == gate)
            .FirstOrDefault();

        if (issue == null)
        {
            return $"- ✓ {gate}\n";
        }

        // The read-only gate names the layer that rejected the SQL (guardrail / ast / empty) so a caller
        // can tell a regex backstop hit from an AST rejection.
        return gate == ReadOnlyGate
            ? $"- ✗ {gate} [{issue.Code}] — {issue.Error}\n"
            : $"- ✗ {gate} — {issue.Error}\n";
    }

    // Maps the collected gate issues onto McpQuerySignal's failure taxonomy: the schema gate maps to
    // SchemaValidationFailed, the provider dry-run to DryRunFailed, and the read-only gate to
    // ExecutionFailed — the same fields the ask flow populates, so dry_run rows aggregate alongside ask
    // rows in the learning loop. A SKIPPED provider gate is NOT a dry-run failure: the query was never
    // presented to the provider, so the dry-run failure fields stay unset (the advisory issue still
    // makes the verdict invalid; recording it as DryRunFailed would poison the learning loop with a
    // failure that never happened).
    private static void RecordGateFailuresOnSignal(
        McpSignalBuilder signal, IReadOnlyList<GateIssue> issues, bool providerGateSkipped)
    {
        foreach (var issue in issues)
        {
            switch (issue.Gate)
            {
                case SchemaGate:
                    signal.SetSchemaValidationFailed(issue.Error);
                    break;
                case ProviderGate:
                    if (!providerGateSkipped)
                    {
                        signal.SetDryRunFailed(issue.Error);
                    }

                    break;
                default:
                    signal.SetExecutionFailed(issue.Error);
                    break;
            }
        }
    }

    // Machine-readable companion to the markdown verdict:
    // { valid, issues: [{gate, code, error}], executable_sql: string|null, pii_columns: [string] }.
    private static JsonNode BuildStructuredContent(
        bool valid,
        IReadOnlyList<GateIssue> issues,
        string? executableSql,
        IReadOnlyList<string> piiColumns)
    {
        var issuesNode = new JsonArray();
        foreach (var issue in issues)
        {
            issuesNode.Add(new JsonObject
            {
                ["gate"] = issue.Gate,
                ["code"] = issue.Code,
                ["error"] = issue.Error
            });
        }

        return new JsonObject
        {
            ["valid"] = valid,
            ["issues"] = issuesNode,
            ["executable_sql"] = executableSql,
            ["pii_columns"] = new JsonArray(piiColumns.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        };
    }

    private sealed record GateIssue(string Gate, string Code, string Error);
}
