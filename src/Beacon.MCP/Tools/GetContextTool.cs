using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class GetContextTool(
    IKnowledgeGraphService knowledgeGraph,
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectContext projectContext,
    McpProjectContextManager sessionManager,
    McpAuditService auditService)
{
    [McpServerTool(Name = "get_context")]
    [Description("Get an overview of the project: its data sources, schemas, tables, quality scores, and documentation status. This is the starting point for understanding what data is available.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("Optional. If your API key has access to multiple projects, specify which one.")]
        int? project_id = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var resolveError = ToolHelper.ResolveProjectId(projectContext, sessionManager, project_id, out var projectId);
        if (resolveError != null) return ToolHelper.Error(resolveError);

        // No McpSignalService call here: McpQuerySignal models the SQL query-learning loop
        // (generated SQL, intent, routing, validation/execution outcomes). This read-only project
        // overview produces none of those signals, so a signal would only add empty rows. Audit-only.
        // This is the canonical explanation for the read-only project tools; ProjectSearchTool and
        // ProjectGetDocumentationTool point here rather than repeating it.
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var project = await context.Projects
                .Where(p => p.Id == projectId)
                .Select(p => new { p.Name, p.Description })
                .FirstOrDefaultAsync(cancellationToken);

            if (project == null)
            {
                sw.Stop();
                await auditService.LogToolCallAsync(null, projectContext.UserId, "get_context",
                    project_id?.ToString(), null, projectId, (int)sw.ElapsedMilliseconds, null, $"Project {projectId} not found.", cancellationToken);
                return ToolHelper.Error($"Project {projectId} not found.");
            }

            var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, cancellationToken);

            var hasDoc = await context.ProjectDocumentations
                .AnyAsync(d => d.ProjectId == projectId, cancellationToken);

            var repoCount = await context.GitHubRepositories
                .CountAsync(r => r.ProjectId == projectId, cancellationToken);

            var text = $"# Project: {project.Name}\n";
            if (!string.IsNullOrEmpty(project.Description))
                text += $"{project.Description}\n";
            text += "\n";

            text += $"**Data Sources:** {dataSources.Count}\n";
            text += $"**Documentation:** {(hasDoc ? "Generated" : "Not yet generated")}\n";
            text += $"**Repositories:** {repoCount}\n\n";

            text += "## Data Sources\n\n";
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

            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "get_context",
                project_id?.ToString(), null, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            return ToolHelper.Success(text);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "get_context",
                project_id?.ToString(), null, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            return ToolHelper.Error(ex.Message);
        }
    }
}
