using System.Text;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Resources;

internal sealed class ProjectResources(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph) : IMcpResource
{
    public bool CanHandle(string uri) => uri.StartsWith("semantico://project");

    public async Task<List<McpResource>> ListAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var projects = await context.Projects.ToListAsync(ct);

        return projects.Select(p => new McpResource
        {
            Uri = $"semantico://project/{p.Id}/report",
            Name = $"{p.Name} - Report",
            Description = $"Comprehensive report for project {p.Name}",
            MimeType = "text/markdown"
        }).ToList();
    }

    public async Task<McpResourceContent?> ReadAsync(string uri, CancellationToken ct)
    {
        var parts = uri.Replace("semantico://project/", "").Split('/');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var projectId))
            return null;

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var project = await context.Projects
            .Include(p => p.DataSources).ThenInclude(ds => ds.DataSource)
            .Include(p => p.Repositories)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project == null) return null;

        var sb = new StringBuilder($"# Project Report: {project.Name}\n\n");
        if (project.Description != null) sb.AppendLine($"{project.Description}\n");

        sb.AppendLine("## Data Sources\n");
        foreach (var pds in project.DataSources)
        {
            var dsKnowledge = await knowledgeGraph.GetDataSourceKnowledgeAsync(pds.DataSourceId, ct);
            sb.AppendLine($"### {dsKnowledge.Name}");
            sb.AppendLine($"- Engine: {dsKnowledge.DatabaseEngine}");
            sb.AppendLine($"- Tables: {dsKnowledge.TableCount}");
            if (dsKnowledge.OverallQualityScore.HasValue)
                sb.AppendLine($"- Quality: {dsKnowledge.OverallQualityScore:F0}%");
            sb.AppendLine($"- Documentation: {(dsKnowledge.HasDocumentation ? "Yes" : "No")}");
            sb.AppendLine($"- Code References: {dsKnowledge.CodeReferenceCount}\n");
        }

        if (project.Repositories.Count > 0)
        {
            sb.AppendLine("## Repositories\n");
            foreach (var repo in project.Repositories)
            {
                sb.AppendLine($"- **{repo.RepositoryUrl}** ({repo.Branch})");
                sb.AppendLine($"  Scan: {repo.ScanStatus}, Files: {repo.TotalFilesScanned}, References: {repo.TotalReferencesFound}");
            }
        }

        return new McpResourceContent
        {
            Uri = uri,
            MimeType = "text/markdown",
            Text = sb.ToString()
        };
    }
}
