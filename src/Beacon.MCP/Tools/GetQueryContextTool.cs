using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class GetQueryContextTool(
    IKnowledgeGraphService knowledgeGraph,
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectContext projectContext,
    McpAuditService auditService,
    ILogger<GetQueryContextTool> logger)
{
    private const int DefaultMaxChars = 12000;
    private const int MinMaxChars = 1000;
    private const int MaxMaxChars = 30000;

    [McpServerTool(Name = "get_query_context", Title = "Get Grounding Context for a Question", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Get the grounding context Beacon uses for SQL generation, scoped to your question: table schemas with real sample values, verified join paths, human-verified example queries, learned patterns, and the business glossary. Use it to write your own SQL, then validate with dry_run and execute with query.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("The question you plan to answer with SQL — the returned context is retrieved and ranked against it")]
        string question,
        [Description("Name of the data source to ground against (preferred when the project has several)")]
        string? datasource_name = null,
        [Description("ID of the data source to ground against (alternative to name)")]
        int? datasource_id = null,
        [Description("Optional. Specify project if your API key has access to multiple projects.")]
        int? project_id = null,
        [Description("Maximum characters of grounding context to return (default 12000, min 1000, max 30000)")]
        int? max_chars = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // No McpSignalService call here: this tool reads the assembled grounding context without
        // generating or executing SQL, so it produces none of the SQL-learning-loop outcomes
        // McpQuerySignal models. Audit-only — see GetContextTool for the canonical rationale.
        // RECORDED WAIVER (codex PR-11 R4): the review suggested recording signals here too; declined —
        // read tools stay audit-only per the documented convention above. dry_run, by contrast, DOES
        // record signals: caller-authored SQL passing/failing the gates is exactly what the loop learns from.
        if (string.IsNullOrEmpty(question))
        {
            return await FailAsync(sw, null, datasource_id, question, "Missing required parameter: question", cancellationToken);
        }

        var resolveError = ToolHelper.ResolveProjectId(projectContext, project_id, out var projectId);
        if (resolveError != null)
        {
            return await FailAsync(sw, null, datasource_id, question, resolveError, cancellationToken);
        }

        int dataSourceId;
        if (datasource_id != null)
        {
            var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, datasource_id.Value, cancellationToken);
            if (projectError != null)
            {
                return await FailAsync(sw, projectId, datasource_id, question, projectError, cancellationToken);
            }

            dataSourceId = datasource_id.Value;
        }
        else if (!string.IsNullOrEmpty(datasource_name))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, datasource_name, cancellationToken);
            if (nameError != null)
            {
                return await FailAsync(sw, projectId, null, question, nameError, cancellationToken);
            }

            dataSourceId = resolvedId;
        }
        else
        {
            var sourceIds = await ToolHelper.GetProjectDataSourceIdsAsync(contextFactory, projectId, cancellationToken);
            if (sourceIds.Count == 0)
            {
                return await FailAsync(sw, projectId, null, question, "No data sources in this project.", cancellationToken);
            }

            if (sourceIds.Count > 1)
            {
                var listing = await ListDataSourcesAsync(sourceIds, cancellationToken);
                return await FailAsync(sw, projectId, null, question,
                    $"This project has {sourceIds.Count} data sources: {listing}. Pass datasource_name or datasource_id to select one.",
                    cancellationToken);
            }

            dataSourceId = sourceIds[0];
        }

        try
        {
            var maxChars = Math.Clamp(max_chars ?? DefaultMaxChars, MinMaxChars, MaxMaxChars);

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var dataSourceName = await context.DataSources
                .Where(x => x.Id == dataSourceId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

            var smartContext = await knowledgeGraph.GetSmartContextForAskAsync(dataSourceId, projectId, question, cancellationToken);

            // The max_chars budget applies to the grounding context itself; the fixed header and the
            // truncation note ride outside it, so raising max_chars is a pure context gain.
            var dialect = smartContext.DatabaseDialect ?? "SQL";
            var (contextText, truncated) = TrimToBudget(smartContext.FullContext, maxChars);

            var text = $"# Query Context: {dataSourceName} ({dialect})\n" +
                $"Write {dialect} SELECT statements. Validate with dry_run, execute with query. Sections marked (authoritative) are human-verified.\n\n" +
                contextText;

            if (truncated)
            {
                text += $"\n\n_Truncated at {maxChars} chars. Raise max_chars (up to {MaxMaxChars}) for the full context._";
            }

            var structured = new JsonObject
            {
                ["data_source_id"] = dataSourceId,
                ["data_source"] = dataSourceName,
                ["dialect"] = smartContext.DatabaseDialect,
                ["truncated"] = truncated
            };

            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "get_query_context",
                question, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            return ToolHelper.Success(text, structured);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "get_query_context",
                question, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            // §1.11 — ex.Message can quote user input; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "get_query_context", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "get_query_context"));
        }
    }

    // §1.7 — audit must be recorded on every outcome, including the early-exit failures before the
    // context assembly (missing question, project/data-source resolution, access denied). Audit-only,
    // no signal — see the rationale at the top of ExecuteAsync.
    private async Task<CallToolResult> FailAsync(
        Stopwatch sw,
        int? projectId,
        int? dataSourceId,
        string question,
        string error,
        CancellationToken cancellationToken)
    {
        sw.Stop();
        await auditService.LogToolCallAsync(null, projectContext.UserId, "get_query_context",
            question, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, error, cancellationToken);
        return ToolHelper.Error(error);
    }

    private async Task<string> ListDataSourcesAsync(List<int> sourceIds, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sources = await context.DataSources
            .Where(x => sourceIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Name
                })
            .ToListAsync(cancellationToken);

        return string.Join(", ", sources.Select(x => $"{x.Id}: {x.Name}"));
    }

    // Cuts the context to the budget at the LAST section boundary ("\n## ") inside it, so a
    // partially-cut section is dropped rather than served looking complete. Falls back to the last
    // line boundary, then to a hard cut.
    private static (string Text, bool Truncated) TrimToBudget(string context, int maxChars)
    {
        if (context.Length <= maxChars)
        {
            return (context, false);
        }

        var window = context[..maxChars];
        var sectionBoundary = window.LastIndexOf("\n## ", StringComparison.Ordinal);
        if (sectionBoundary > 0)
        {
            return (window[..sectionBoundary], true);
        }

        var lineBoundary = window.LastIndexOf('\n');
        if (lineBoundary > 0)
        {
            return (window[..lineBoundary], true);
        }

        // Never split a surrogate pair on the hard cut: if the char AT the boundary is a low
        // surrogate, the kept text would end with its dangling high surrogate — back off one char
        // so the whole pair is dropped instead of emitting invalid UTF-16. Mirrors
        // ProjectGetDocumentationTool.TruncateForConcise.
        if (char.IsLowSurrogate(context[maxChars]))
        {
            return (window[..^1], true);
        }

        return (window, true);
    }
}
