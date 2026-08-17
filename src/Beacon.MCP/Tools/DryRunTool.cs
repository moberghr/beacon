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
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class DryRunTool(
    IDbContextFactory<BeaconContext> contextFactory,
    IQueryGuardrailService guardrailService,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    SqlSchemaValidator schemaValidator,
    IKnowledgeGraphService knowledgeGraph,
    IQueryExecutionService queryExecutionService,
    IMcpSettingsProvider settingsProvider,
    IProjectContext projectContext,
    McpAuditService auditService,
    ILogger<DryRunTool> logger)
{
    // The dry-run preview applies the same default row budget the query tool uses, capped by settings.
    private const int DefaultMaxRows = 100;

    [McpServerTool(Name = "dry_run", Title = "Validate SQL Without Executing", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Validate a SQL query through all of Beacon's safety gates — read-only guardrail, AST validation, schema column check, and a provider dry-run (EXPLAIN) — without executing it. Returns the exact SQL that would run (with the row limit applied) and any issues found. If the data source has no extracted schema metadata yet, the schema gate reports an advisory issue and the verdict is invalid (the column check could not be performed); the provider dry-run still runs. Use before query.")]
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

        // No McpSignalService call here: dry_run validates caller-authored SQL without generating or
        // executing anything, so it produces none of the SQL-learning-loop outcomes McpQuerySignal
        // models. Audit-only — see GetContextTool for the canonical rationale.
        if (string.IsNullOrEmpty(sql))
        {
            return await FailAsync(sw, null, datasource_id, sql, "Missing required parameter: sql", cancellationToken);
        }

        var resolveError = ToolHelper.ResolveProjectId(projectContext, project_id, out var projectId);
        if (resolveError != null)
        {
            return await FailAsync(sw, null, datasource_id, sql, resolveError, cancellationToken);
        }

        if (datasource_id == null && string.IsNullOrEmpty(datasource_name))
        {
            return await FailAsync(sw, projectId, null, sql, "Provide either datasource_name or datasource_id.", cancellationToken);
        }

        if (datasource_id == null && !string.IsNullOrEmpty(datasource_name))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, datasource_name, cancellationToken);
            if (nameError != null)
            {
                return await FailAsync(sw, projectId, null, sql, nameError, cancellationToken);
            }

            datasource_id = resolvedId;
        }

        var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, datasource_id!.Value, cancellationToken);
        if (projectError != null)
        {
            return await FailAsync(sw, projectId, datasource_id, sql, projectError, cancellationToken);
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
                return await FailAsync(sw, projectId, datasource_id, sql,
                    "dry_run validates SQL only — API data sources are not supported.", cancellationToken);
            }

            var dialect = dataSource.DatabaseEngineType?.ToString();
            var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
            var issues = new List<(string Gate, string Error)>();

            // Gate 1: regex guardrail (read-only backstop + PII detection report)
            var validation = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
            {
                ReadOnly = settings.EnforceReadOnly,
                DetectPii = settings.EnablePiiDetection,
                CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
            });
            if (!validation.IsValid)
            {
                issues.Add(("guardrail", validation.Error ?? "Query validation failed"));
            }

            var piiColumns = validation.PiiColumns ?? [];

            // Gate 2: AST read-only defense-in-depth on top of the regex guardrail (§1.5) — gated on
            // the same setting the query tool uses.
            var astGateRan = settings.EnforceReadOnly;
            if (astGateRan)
            {
                var astError = readOnlyAstValidator.Validate(sql, dialect);
                if (astError != null)
                {
                    issues.Add(("ast", astError));
                }
            }

            // Gate 3: schema-catalog column check — catches hallucinated columns without a DB round-trip.
            // An EMPTY catalog means the check cannot run at all — surface that as an advisory issue
            // (making the verdict invalid: the caller asked for a validation that could not be
            // performed) instead of rendering a vacuous "✓ schema".
            var catalog = await knowledgeGraph.GetSchemaCatalogAsync(datasource_id.Value, cancellationToken);
            var schemaCheckSkipped = catalog.Count == 0;
            if (schemaCheckSkipped)
            {
                issues.Add(("schema", "No schema metadata available for this data source yet — column check was skipped. Run metadata extraction, or verify column names manually."));
            }
            else
            {
                var schemaCheck = schemaValidator.Validate(sql, catalog, dialect);
                if (!schemaCheck.IsValid)
                {
                    issues.Add(("schema", schemaCheck.Error ?? "Schema validation failed"));
                }
            }

            // Gate 4: provider dry-run (EXPLAIN / sp_describe_first_result_set) — ONLY when every
            // previous gate passed; running EXPLAIN on a known-write statement is pointless and unsafe.
            // The schema-skip advisory alone does not block it: the parsers passed, so the provider
            // dry-run is the only column check still available.
            var providerGateRan = false;
            if (issues.Count == 0 || (schemaCheckSkipped && issues.Count == 1))
            {
                providerGateRan = true;
                var providerError = await queryExecutionService.ValidateAsync(datasource_id.Value, sql, cancellationToken);
                if (providerError != null)
                {
                    issues.Add(("provider_dry_run", providerError));
                }
            }

            var valid = issues.Count == 0;
            var maxRows = Math.Min(DefaultMaxRows, settings.MaxRowLimit);
            var executableSql = valid ? guardrailService.ApplyRowLimit(sql, maxRows, dialect) : null;

            var text = BuildMarkdown(valid, issues, astGateRan, providerGateRan, executableSql, piiColumns);
            var structured = BuildStructuredContent(valid, issues, executableSql, piiColumns);

            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "dry_run",
                sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            return ToolHelper.Success(text, structured);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "dry_run",
                sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            // §1.11 — ex.Message can quote the user's SQL; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "dry_run", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "dry_run"));
        }
    }

    // §1.7 — audit must be recorded on every outcome, including the early-exit failures before the
    // gate pipeline (missing input, project/data-source resolution, access denied). Audit-only, no
    // signal — see the rationale at the top of ExecuteAsync.
    private async Task<CallToolResult> FailAsync(
        Stopwatch sw,
        int? projectId,
        int? dataSourceId,
        string? sql,
        string error,
        CancellationToken cancellationToken)
    {
        sw.Stop();
        await auditService.LogToolCallAsync(null, projectContext.UserId, "dry_run",
            sql, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, error, cancellationToken);
        return ToolHelper.Error(error);
    }

    private static string BuildMarkdown(
        bool valid,
        IReadOnlyList<(string Gate, string Error)> issues,
        bool astGateRan,
        bool providerGateRan,
        string? executableSql,
        IReadOnlyList<string> piiColumns)
    {
        var text = valid
            ? "# Dry Run\n\n**VALID** — all safety gates passed. The query was NOT executed.\n\n"
            : $"# Dry Run\n\n**INVALID** — {issues.Count} issue(s) found. The query was NOT executed.\n\n";

        text += GateLine("guardrail", issues);
        text += astGateRan
            ? GateLine("ast", issues)
            : "- – ast — skipped (read-only enforcement disabled)\n";
        text += GateLine("schema", issues);
        text += providerGateRan
            ? GateLine("provider_dry_run", issues)
            : "- – provider_dry_run — skipped (fix the issues above first)\n";

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

    private static string GateLine(string gate, IReadOnlyList<(string Gate, string Error)> issues)
    {
        var error = issues
            .Where(x => x.Gate == gate)
            .Select(x => x.Error)
            .FirstOrDefault();

        return error == null ? $"- ✓ {gate}\n" : $"- ✗ {gate} — {error}\n";
    }

    // Machine-readable companion to the markdown verdict:
    // { valid, issues: [{gate, error}], executable_sql: string|null, pii_columns: [string] }.
    private static JsonNode BuildStructuredContent(
        bool valid,
        IReadOnlyList<(string Gate, string Error)> issues,
        string? executableSql,
        IReadOnlyList<string> piiColumns)
    {
        var issuesNode = new JsonArray();
        foreach (var (gate, error) in issues)
        {
            issuesNode.Add(new JsonObject
            {
                ["gate"] = gate,
                ["error"] = error
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
}
