using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostDocs;
using Beacon.Core.HostEndpoints;
using Beacon.MCP.HostEndpoints;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class GetContextTool(
    IKnowledgeGraphService knowledgeGraph,
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectBriefService projectBriefService,
    IProjectContext projectContext,
    McpToolCallRecorder recorder,
    IHostEndpointToolOverview? hostEndpointTools = null)
{
    internal const string OverviewFormat = "overview";
    internal const string AgentsMdFormat = "agents_md";
    internal const int MaxDocuments = 50;
    internal const int MaxTools = 50;

    [McpServerTool(Name = "get_context", Title = "Project Overview", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Get an overview of the project: its data sources, schemas, tables, quality scores and documentation status, the host application tools it serves and its imported documents. This is the starting point for understanding what the project offers. format='agents_md' returns a project brief in markdown for an agent workspace's AGENTS.md instead.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("Optional. If your API key has access to multiple projects, specify which one.")]
        int? project_id = null,
        [Description("Optional. 'overview' (default) or 'agents_md' — a deterministic project brief (data sources, key tables, glossary, documents, tool guidance) to drop into AGENTS.md.")]
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        // Written by McpAuditCallToolFilter after the tool returns or throws (§1.7).
        recorder.Start("get_context", projectContext.UserId, project_id?.ToString());
        var resolveError = ToolHelper.ResolveProjectId(projectContext, "get_context", project_id, out var projectId);
        if (resolveError != null)
        {
            recorder.Fail(resolveError);
            return ToolHelper.Error(resolveError);
        }

        recorder.WithProject(projectId);

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? OverviewFormat : format.Trim().ToLowerInvariant();
        if (normalizedFormat != OverviewFormat && normalizedFormat != AgentsMdFormat)
        {
            var formatError = $"format must be '{OverviewFormat}' or '{AgentsMdFormat}'.";
            recorder.WithParameters(format).Fail(formatError);
            return ToolHelper.Error(formatError);
        }

        if (normalizedFormat == AgentsMdFormat)
        {
            return await GetProjectBriefAsync(projectId, cancellationToken);
        }

        // No McpSignalService call here: McpQuerySignal models the SQL query-learning loop
        // (generated SQL, intent, routing, validation/execution outcomes). This read-only project
        // overview produces none of those signals, so a signal would only add empty rows. Audit-only.
        // This is the canonical explanation for the read-only project tools; ProjectSearchTool and
        // ProjectGetDocumentationTool point here rather than repeating it.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Name, p.Description })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
        {
            recorder.Fail($"Project {projectId} not found.");
            return ToolHelper.Error($"Project {projectId} not found.");
        }

        var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, cancellationToken);

        var hasDoc = await context.ProjectDocumentations
            .AnyAsync(d => d.ProjectId == projectId, cancellationToken);

        var repoCount = await context.GitHubRepositories
            .CountAsync(r => r.ProjectId == projectId, cancellationToken);

        var documents = await ImportedDocumentQueries.ListForProject(context, projectId)
            .Take(MaxDocuments)
            .ToListAsync(cancellationToken);

        var documentCount = documents.Count < MaxDocuments
            ? documents.Count
            : await context.ProjectImportedDocuments
                .Where(x => x.ProjectId == projectId)
                .CountAsync(cancellationToken);

        // The tools this caller's tools/list offers: none for a credential that cannot call them, no write tool for a
        // system caller.
        var endpointTools = hostEndpointTools == null
            ? null
            : await hostEndpointTools.ListForProjectAsync(projectId, cancellationToken);

        var text = $"# Project: {project.Name}\n";
        if (!string.IsNullOrEmpty(project.Description))
            text += $"{project.Description}\n";
        text += "\n";

        // A project without data sources (an MCP gateway's, or one of documents only) has no SQL side to report: its
        // overview is the host application tools and the documents it serves.
        var summary = "";
        if (dataSources.Count > 0)
        {
            summary += $"**Data Sources:** {dataSources.Count}\n";
            summary += $"**Documentation:** {(hasDoc ? "Generated" : "Not yet generated")}\n";
            summary += $"**Repositories:** {repoCount}\n";
        }
        else if (hasDoc)
        {
            summary += "**Documentation:** Generated\n";
        }

        if (endpointTools != null)
        {
            summary += $"**Host application tools:** {endpointTools.Tools.Count}\n";
        }

        if (documentCount > 0)
        {
            summary += $"**Imported documents:** {documentCount}\n";
        }

        text += summary.Length > 0
            ? summary + "\n"
            : "This project has no data sources, host application tools or imported documents yet.\n";

        if (dataSources.Count > 0)
        {
            text += "## Data Sources\n\n";
        }

        foreach (var ds in dataSources)
        {
            var isApi = ds.DataSourceType == DataSourceType.Api;
            text += $"### {ds.Name} (ID: {ds.DataSourceId})\n";
            text += $"- **Type:** {ds.DatabaseEngine ?? ds.DataSourceType.ToString()}\n";
            text += $"- **{(isApi ? "Endpoints" : "Tables")}:** {ds.TableCount}\n";
            if (ds.OverallQualityScore.HasValue)
                text += $"- **Quality:** {ds.OverallQualityScore:F0}%\n";
            text += $"- **Code References:** {ds.CodeReferenceCount}\n";

            if (ds.Schemas.Count > 0)
            {
                text += $"- **{(isApi ? "Tags" : "Schemas")}:** ";
                text += string.Join(", ", ds.Schemas.Select(s =>
                    $"{s.SchemaName} ({s.TableCount} {(isApi ? "endpoints" : "tables")})"));
                text += "\n";
            }
            text += "\n";
        }

        text += EndpointToolsSection(endpointTools);
        text += DocumentsSection(documents, documentCount);

        recorder.Succeed();
        return ToolHelper.Success(text);
    }

    // The brief is built from stored metadata only (no LLM) and audited like the overview, failure path included (a throw
    // is recorded by McpAuditCallToolFilter).
    private async Task<CallToolResult> GetProjectBriefAsync(int projectId, CancellationToken cancellationToken)
    {
        recorder.WithParameters(AgentsMdFormat);
        var brief = await projectBriefService.BuildAgentsMdAsync(projectId, cancellationToken);
        if (brief == null)
        {
            var error = $"Project {projectId} not found.";
            recorder.Fail(error);

            return ToolHelper.Error(error);
        }

        recorder.Succeed();

        return ToolHelper.Success(brief);
    }

    private static string EndpointToolsSection(HostEndpointToolListing? listing)
    {
        if (listing == null)
        {
            return string.Empty;
        }

        var text = "## Host application tools\n\n";
        text += listing.CatalogMode
            ? $"Find one with `{HostEndpointToolService.SearchToolName}` and run it with `{HostEndpointToolService.CallToolName}`. Tools marked (writes) change data.\n\n"
            : "Call them by name. Tools marked (writes) change data.\n\n";

        foreach (var tool in listing.Tools.Take(MaxTools))
        {
            text += $"- `{(listing.CatalogMode ? tool.Name : tool.ToolName)}`{(tool.ReadOnly ? "" : " (writes)")}";
            text += string.IsNullOrWhiteSpace(tool.Description) ? "\n" : $" — {tool.Description.Trim()}\n";
        }

        if (listing.Tools.Count > MaxTools)
        {
            text += $"- … {listing.Tools.Count - MaxTools} more; `{HostEndpointToolService.SearchToolName}` finds them.\n";
        }

        return text + "\n";
    }

    private static string DocumentsSection(IReadOnlyList<ImportedDocumentSummary> documents, int documentCount)
    {
        if (documents.Count == 0)
        {
            return string.Empty;
        }

        var text = "## Imported documents\n\n";
        text += "Read one with `get_documentation` and `document: \"<path>\"`; `search` matches their text.\n\n";
        foreach (var document in documents)
        {
            text += $"- {document.Title} — `{document.Path}`\n";
        }

        if (documentCount > documents.Count)
        {
            text += $"- … {documentCount - documents.Count} more; `get_documentation` lists them all.\n";
        }

        return text + "\n";
    }
}
