using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Documentation;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class GetContextTool(
    IKnowledgeGraphService knowledgeGraph,
    IDbContextFactory<SemanticoContext> contextFactory) : IMcpTool
{
    public string Name => "get_context";
    public string Description => "Get an overview of the project: its data sources, schemas, tables, quality scores, and documentation status. This is the starting point for understanding what data is available.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["project_id"] = ToolHelper.IntProp("Optional. If your API key has access to multiple projects, specify which one.")
        });

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var requestedProjectId = ToolHelper.GetInt(arguments, "project_id");
        var resolveError = ToolHelper.ResolveProjectId(session, requestedProjectId, out var projectId);
        if (resolveError != null) return resolveError;

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var project = await context.Projects
                .Where(p => p.Id == projectId)
                .Select(p => new { p.Name, p.Description })
                .FirstOrDefaultAsync(ct);

            if (project == null)
                return ToolHelper.ErrorResult($"Project {projectId} not found.");

            var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, ct);

            var hasDoc = await context.ProjectDocumentations
                .AnyAsync(d => d.ProjectId == projectId, ct);

            var repoCount = await context.GitHubRepositories
                .CountAsync(r => r.ProjectId == projectId, ct);

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

            return ToolHelper.TextResult(text);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error: {ex.Message}");
        }
    }
}
