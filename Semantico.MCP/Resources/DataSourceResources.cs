using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Resources;

internal sealed class DataSourceResources(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraph) : IMcpResource
{
    public bool CanHandle(string uri) => uri.StartsWith("semantico://datasource");

    public async Task<List<McpResource>> ListAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSources = await context.DataSources.ToListAsync(ct);

        var resources = new List<McpResource>();

        // List top-level datasources resource
        resources.Add(new McpResource
        {
            Uri = "semantico://datasources",
            Name = "All Data Sources",
            Description = "List of all available data sources",
            MimeType = "text/plain"
        });

        foreach (var ds in dataSources)
        {
            resources.Add(new McpResource
            {
                Uri = $"semantico://datasource/{ds.Id}/schema",
                Name = $"{ds.Name} - Schema",
                Description = $"Full schema for {ds.Name}",
                MimeType = "text/plain"
            });

            resources.Add(new McpResource
            {
                Uri = $"semantico://datasource/{ds.Id}/documentation",
                Name = $"{ds.Name} - Documentation",
                Description = $"AI-generated documentation for {ds.Name}",
                MimeType = "text/markdown"
            });

            resources.Add(new McpResource
            {
                Uri = $"semantico://datasource/{ds.Id}/quality",
                Name = $"{ds.Name} - Quality Report",
                Description = $"Data quality report for {ds.Name}",
                MimeType = "text/plain"
            });
        }

        return resources;
    }

    public async Task<McpResourceContent?> ReadAsync(string uri, CancellationToken ct)
    {
        if (uri == "semantico://datasources")
            return await ReadDataSourceListAsync(ct);

        // Parse: semantico://datasource/{id}/{type}
        var parts = uri.Replace("semantico://datasource/", "").Split('/');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var dsId))
            return null;

        return parts[1] switch
        {
            "schema" => await ReadSchemaAsync(dsId, ct),
            "documentation" => await ReadDocumentationAsync(dsId, ct),
            "quality" => await ReadQualityAsync(dsId, ct),
            _ => null
        };
    }

    private async Task<McpResourceContent> ReadDataSourceListAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSources = await context.DataSources.ToListAsync(ct);

        var sb = new StringBuilder("# Data Sources\n\n");
        foreach (var ds in dataSources)
        {
            sb.AppendLine($"- **{ds.Name}** (ID: {ds.Id})");
            sb.AppendLine($"  Type: {ds.DataSourceType}, Engine: {ds.DatabaseEngineType}");
        }

        return new McpResourceContent
        {
            Uri = "semantico://datasources",
            MimeType = "text/markdown",
            Text = sb.ToString()
        };
    }

    private async Task<McpResourceContent> ReadSchemaAsync(int dsId, CancellationToken ct)
    {
        var text = await knowledgeGraph.GetContextForLlmAsync(dsId, ct: ct);
        return new McpResourceContent
        {
            Uri = $"semantico://datasource/{dsId}/schema",
            MimeType = "text/markdown",
            Text = text
        };
    }

    private async Task<McpResourceContent> ReadDocumentationAsync(int dsId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var docs = await context.DataSourceDocumentations
            .Where(d => d.DataSourceId == dsId)
            .Include(d => d.Sections)
            .FirstOrDefaultAsync(ct);

        var sb = new StringBuilder();
        if (docs == null)
        {
            sb.AppendLine("No documentation generated yet. Use the Semantico UI to generate AI documentation.");
        }
        else
        {
            sb.AppendLine($"# Documentation: {docs.DataSource?.Name ?? "Data Source"}");
            sb.AppendLine($"Generated: {docs.CreatedTime:yyyy-MM-dd}\n");

            foreach (var section in docs.Sections.OrderBy(s => s.SortOrder))
            {
                sb.AppendLine($"## {section.Title}");
                sb.AppendLine(section.GetDisplayContent());
                sb.AppendLine();
            }
        }

        return new McpResourceContent
        {
            Uri = $"semantico://datasource/{dsId}/documentation",
            MimeType = "text/markdown",
            Text = sb.ToString()
        };
    }

    private async Task<McpResourceContent> ReadQualityAsync(int dsId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var scores = await context.DataQualityScores
            .Where(q => q.DataSourceId == dsId)
            .OrderByDescending(q => q.Score)
            .ToListAsync(ct);

        var sb = new StringBuilder("# Data Quality Report\n\n");
        if (scores.Count == 0)
        {
            sb.AppendLine("No data quality scores. Set up Data Contracts in the Semantico UI.");
        }
        else
        {
            sb.AppendLine("| Table | Score | Trend | Last Evaluated |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in scores)
                sb.AppendLine($"| {s.SchemaName}.{s.TableName} | {s.Score:F0}% | {s.TrendDirection} | {s.EvaluatedAt:yyyy-MM-dd HH:mm} |");
        }

        return new McpResourceContent
        {
            Uri = $"semantico://datasource/{dsId}/quality",
            MimeType = "text/markdown",
            Text = sb.ToString()
        };
    }
}
