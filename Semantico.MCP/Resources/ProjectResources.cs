using System.Text;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Semantico.AI.Services.Documentation;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;

namespace Semantico.MCP.Resources;

[McpServerResourceType]
internal sealed class ProjectResources(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph,
    IProjectDocumentationService documentationService)
{
    [McpServerResource(UriTemplate = "semantico://project/{projectId}/documentation",
        Name = "Project Documentation", MimeType = "text/markdown")]
    public async Task<string> ReadDocumentationAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var markdown = await documentationService.ExportLatestToMarkdownAsync(projectId, cancellationToken);
        return markdown ?? "No documentation generated yet. Use the Semantico UI to generate project documentation.";
    }

    [McpServerResource(UriTemplate = "semantico://project/{projectId}/schema",
        Name = "Project Schema", MimeType = "text/markdown")]
    public async Task<string> ReadSchemaAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return await knowledgeGraph.GetProjectContextForLlmAsync(projectId, cancellationToken);
    }

    [McpServerResource(UriTemplate = "semantico://project/{projectId}/quality",
        Name = "Data Quality Report", MimeType = "text/markdown")]
    public async Task<string> ReadQualityAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dsIds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(cancellationToken);

        var scores = await context.DataQualityScores
            .Where(q => dsIds.Contains(q.DataSourceId))
            .OrderByDescending(q => q.Score)
            .ToListAsync(cancellationToken);

        var sb = new StringBuilder("# Data Quality Report\n\n");
        if (scores.Count == 0)
        {
            sb.AppendLine("No data quality scores. Set up Data Contracts in the Semantico UI.");
        }
        else
        {
            sb.AppendLine("| Data Source | Table | Score | Trend | Last Evaluated |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var s in scores)
                sb.AppendLine($"| {s.DataSourceId} | {s.SchemaName}.{s.TableName} | {s.Score:F0}% | {s.TrendDirection} | {s.EvaluatedAt:yyyy-MM-dd HH:mm} |");
        }

        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "semantico://project/{projectId}/report",
        Name = "Project Report", MimeType = "text/markdown")]
    public async Task<string> ReadReportAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Name, p.Description })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
            return "Project not found.";

        var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, cancellationToken);
        var repos = await context.GitHubRepositories
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var sb = new StringBuilder($"# Project Report: {project.Name}\n\n");
        if (project.Description != null) sb.AppendLine($"{project.Description}\n");

        sb.AppendLine("## Data Sources\n");
        foreach (var ds in dataSources)
        {
            sb.AppendLine($"### {ds.Name}");
            sb.AppendLine($"- Engine: {ds.DatabaseEngine}");
            sb.AppendLine($"- Tables: {ds.TableCount}");
            if (ds.OverallQualityScore.HasValue)
                sb.AppendLine($"- Quality: {ds.OverallQualityScore:F0}%");
            sb.AppendLine($"- Documentation: {(ds.HasDocumentation ? "Yes" : "No")}");
            sb.AppendLine($"- Code References: {ds.CodeReferenceCount}\n");
        }

        if (repos.Count > 0)
        {
            sb.AppendLine("## Repositories\n");
            foreach (var repo in repos)
            {
                sb.AppendLine($"- **{repo.RepositoryUrl}** ({repo.Branch})");
                sb.AppendLine($"  Scan: {repo.ScanStatus}, Files: {repo.TotalFilesScanned}, References: {repo.TotalReferencesFound}");
            }
        }

        return sb.ToString();
    }
}
