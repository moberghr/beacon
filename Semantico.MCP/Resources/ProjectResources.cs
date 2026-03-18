using System.Text;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Documentation;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Resources;

internal sealed class ProjectResources(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph,
    IProjectDocumentationService documentationService) : IMcpResource
{
    public bool CanHandle(string uri) => uri.StartsWith("semantico://project");

    public async Task<List<McpResource>> ListAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var projects = await context.Projects
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var resources = new List<McpResource>();
        foreach (var p in projects)
        {
            resources.Add(new McpResource
            {
                Uri = $"semantico://project/{p.Id}/documentation",
                Name = $"{p.Name} - Documentation",
                Description = $"AI-generated documentation for project {p.Name}",
                MimeType = "text/markdown"
            });

            resources.Add(new McpResource
            {
                Uri = $"semantico://project/{p.Id}/schema",
                Name = $"{p.Name} - Schema",
                Description = $"Full schema across all data sources in {p.Name}",
                MimeType = "text/markdown"
            });

            resources.Add(new McpResource
            {
                Uri = $"semantico://project/{p.Id}/quality",
                Name = $"{p.Name} - Quality Report",
                Description = $"Data quality report for project {p.Name}",
                MimeType = "text/markdown"
            });

            resources.Add(new McpResource
            {
                Uri = $"semantico://project/{p.Id}/report",
                Name = $"{p.Name} - Report",
                Description = $"Comprehensive report for project {p.Name}",
                MimeType = "text/markdown"
            });
        }

        return resources;
    }

    public async Task<McpResourceContent?> ReadAsync(string uri, CancellationToken ct)
    {
        var parts = uri.Replace("semantico://project/", "").Split('/');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var projectId))
            return null;

        return parts[1] switch
        {
            "documentation" => await ReadDocumentationAsync(projectId, ct),
            "schema" => await ReadSchemaAsync(projectId, ct),
            "quality" => await ReadQualityAsync(projectId, ct),
            "report" => await ReadReportAsync(projectId, ct),
            _ => null
        };
    }

    private async Task<McpResourceContent> ReadDocumentationAsync(int projectId, CancellationToken ct)
    {
        var markdown = await documentationService.ExportLatestToMarkdownAsync(projectId, ct);

        return new McpResourceContent
        {
            Uri = $"semantico://project/{projectId}/documentation",
            MimeType = "text/markdown",
            Text = markdown ?? "No documentation generated yet. Use the Semantico UI to generate project documentation."
        };
    }

    private async Task<McpResourceContent> ReadSchemaAsync(int projectId, CancellationToken ct)
    {
        var text = await knowledgeGraph.GetProjectContextForLlmAsync(projectId, ct);

        return new McpResourceContent
        {
            Uri = $"semantico://project/{projectId}/schema",
            MimeType = "text/markdown",
            Text = text
        };
    }

    private async Task<McpResourceContent> ReadQualityAsync(int projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dsIds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        var scores = await context.DataQualityScores
            .Where(q => dsIds.Contains(q.DataSourceId))
            .OrderByDescending(q => q.Score)
            .ToListAsync(ct);

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

        return new McpResourceContent
        {
            Uri = $"semantico://project/{projectId}/quality",
            MimeType = "text/markdown",
            Text = sb.ToString()
        };
    }

    private async Task<McpResourceContent> ReadReportAsync(int projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var project = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Name, p.Description })
            .FirstOrDefaultAsync(ct);

        if (project == null)
            return new McpResourceContent
            {
                Uri = $"semantico://project/{projectId}/report",
                MimeType = "text/markdown",
                Text = "Project not found."
            };

        var dataSources = await knowledgeGraph.GetProjectDataSourcesAsync(projectId, ct);
        var repos = await context.GitHubRepositories
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(ct);

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

        return new McpResourceContent
        {
            Uri = $"semantico://project/{projectId}/report",
            MimeType = "text/markdown",
            Text = sb.ToString()
        };
    }
}
