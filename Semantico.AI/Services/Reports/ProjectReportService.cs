using System.Text;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.Projects;
using Semantico.Core.Data.Enums;

namespace Semantico.AI.Services.Reports;

/// <summary>
/// Generates comprehensive project reports by aggregating knowledge graph data,
/// schema metadata, data quality scores, and code-to-data lineage.
/// </summary>
internal sealed class ProjectReportService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IKnowledgeGraphService knowledgeGraphService,
    ILogger<ProjectReportService> logger) : IProjectReportService
{
    /// <summary>
    /// Generates a project report and persists it, returning the new report ID.
    /// </summary>
    public async Task<int> GenerateReportAsync(
        int projectId,
        ReportFormat format = ReportFormat.Markdown,
        ReportType type = ReportType.Full,
        CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var project = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.Name, p.Description })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Project {projectId} not found");

        var dataSourceIds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        logger.LogInformation(
            "Generating {Type} report for Project {ProjectId} ({DataSourceCount} data sources)",
            type, projectId, dataSourceIds.Count);

        var markdown = await BuildMarkdownAsync(project.Name, project.Description, dataSourceIds, type, ct);
        var content = format == ReportFormat.Html ? ConvertToHtml(markdown) : markdown;

        var report = new ProjectReport
        {
            ProjectId = projectId,
            ReportType = type,
            ReportFormat = format,
            GeneratedAt = DateTime.UtcNow,
            Content = content
        };

        context.ProjectReports.Add(report);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Project report {ReportId} generated for Project {ProjectId}", report.Id, projectId);

        return report.Id;
    }

    /// <summary>
    /// Retrieves the content of a previously generated report.
    /// </summary>
    public async Task<string?> GetReportContentAsync(int reportId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.ProjectReports
            .Where(r => r.Id == reportId)
            .Select(r => r.Content)
            .FirstOrDefaultAsync(ct);
    }

    // --- Private helpers ---

    private async Task<string> BuildMarkdownAsync(
        string projectName,
        string? projectDescription,
        List<int> dataSourceIds,
        ReportType type,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {projectName} — Project Report");
        sb.AppendLine();
        sb.AppendLine($"*Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC*");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(projectDescription))
        {
            sb.AppendLine(projectDescription);
            sb.AppendLine();
        }

        if (type is ReportType.Full or ReportType.SchemaOnly or ReportType.QualityOnly)
        {
            await AppendDataSourcesOverviewAsync(sb, dataSourceIds, ct);
        }

        if (type is ReportType.Full or ReportType.SchemaOnly)
        {
            await AppendSchemaCatalogAsync(sb, dataSourceIds, ct);
        }

        if (type is ReportType.Full or ReportType.QualityOnly)
        {
            await AppendDataQualityDashboardAsync(sb, dataSourceIds, ct);
        }

        if (type is ReportType.Full or ReportType.LineageOnly)
        {
            await AppendCodeToDataLineageAsync(sb, dataSourceIds, ct);
        }

        if (type == ReportType.Full)
        {
            await AppendSchemaChangesAsync(sb, dataSourceIds, ct);
            AppendRecommendations(sb);
        }

        return sb.ToString();
    }

    private async Task AppendDataSourcesOverviewAsync(StringBuilder sb, List<int> dataSourceIds, CancellationToken ct)
    {
        sb.AppendLine("## Data Sources Overview");
        sb.AppendLine();

        foreach (var dsId in dataSourceIds)
        {
            try
            {
                var knowledge = await knowledgeGraphService.GetDataSourceKnowledgeAsync(dsId, ct);
                sb.AppendLine($"### {knowledge.Name}");
                if (knowledge.DatabaseEngine != null)
                    sb.AppendLine($"- **Engine:** {knowledge.DatabaseEngine}");
                sb.AppendLine($"- **Tables:** {knowledge.TableCount}");
                if (knowledge.OverallQualityScore.HasValue)
                    sb.AppendLine($"- **Overall Quality:** {knowledge.OverallQualityScore:F0}%");
                sb.AppendLine($"- **Code References:** {knowledge.CodeReferenceCount}");
                sb.AppendLine($"- **Has Documentation:** {(knowledge.HasDocumentation ? "Yes" : "No")}");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load knowledge for DataSource {DataSourceId}", dsId);
            }
        }
    }

    private async Task AppendSchemaCatalogAsync(StringBuilder sb, List<int> dataSourceIds, CancellationToken ct)
    {
        sb.AppendLine("## Schema Catalog");
        sb.AppendLine();

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        foreach (var dsId in dataSourceIds)
        {
            var tables = await context.DatabaseMetadata
                .Where(m => m.DataSourceId == dsId)
                .Select(m => new
                {
                    m.SchemaName,
                    m.TableName,
                    m.TableDescription,
                    Columns = m.Columns
                        .OrderBy(c => c.OrdinalPosition)
                        .Select(c => new
                        {
                            c.ColumnName,
                            c.DataType,
                            c.IsNullable,
                            c.IsPrimaryKey,
                            c.ForeignKeyTable,
                            c.ForeignKeyColumn,
                            c.Description
                        })
                        .ToList()
                })
                .OrderBy(t => t.SchemaName)
                .ThenBy(t => t.TableName)
                .ToListAsync(ct);

            if (tables.Count == 0) continue;

            var dsName = tables.Count > 0
                ? await context.DataSources
                    .Where(ds => ds.Id == dsId)
                    .Select(ds => ds.Name)
                    .FirstOrDefaultAsync(ct) ?? $"DataSource {dsId}"
                : $"DataSource {dsId}";

            sb.AppendLine($"### {dsName}");
            sb.AppendLine();

            foreach (var table in tables)
            {
                sb.AppendLine($"#### `{table.SchemaName}.{table.TableName}`");
                if (!string.IsNullOrWhiteSpace(table.TableDescription))
                    sb.AppendLine($"> {table.TableDescription}");
                sb.AppendLine();
                sb.AppendLine("| Column | Type | Nullable | PK | Notes |");
                sb.AppendLine("|--------|------|----------|----|-------|");

                foreach (var col in table.Columns)
                {
                    var notes = new List<string>();
                    if (col.ForeignKeyTable != null)
                        notes.Add($"FK → {col.ForeignKeyTable}.{col.ForeignKeyColumn}");
                    if (!string.IsNullOrWhiteSpace(col.Description))
                        notes.Add(col.Description);

                    sb.AppendLine($"| {col.ColumnName} | {col.DataType} | {(col.IsNullable ? "Yes" : "No")} | {(col.IsPrimaryKey ? "✓" : "")} | {string.Join("; ", notes)} |");
                }

                sb.AppendLine();
            }
        }
    }

    private async Task AppendDataQualityDashboardAsync(StringBuilder sb, List<int> dataSourceIds, CancellationToken ct)
    {
        sb.AppendLine("## Data Quality Dashboard");
        sb.AppendLine();

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        foreach (var dsId in dataSourceIds)
        {
            var scores = await context.DataQualityScores
                .Where(q => q.DataSourceId == dsId)
                .Select(q => new
                {
                    q.SchemaName,
                    q.TableName,
                    q.Score,
                    q.TrendDirection
                })
                .OrderByDescending(q => q.Score)
                .ToListAsync(ct);

            if (scores.Count == 0) continue;

            var dsName = await context.DataSources
                .Where(ds => ds.Id == dsId)
                .Select(ds => ds.Name)
                .FirstOrDefaultAsync(ct) ?? $"DataSource {dsId}";

            var avgScore = scores.Average(s => s.Score);

            sb.AppendLine($"### {dsName}");
            sb.AppendLine($"**Average Quality Score:** {avgScore:F0}%");
            sb.AppendLine();
            sb.AppendLine("| Schema | Table | Score | Trend |");
            sb.AppendLine("|--------|-------|-------|-------|");

            foreach (var s in scores)
                sb.AppendLine($"| {s.SchemaName} | {s.TableName} | {s.Score:F0}% | {s.TrendDirection} |");

            sb.AppendLine();

            // Active data contracts summary
            var contracts = await context.DataContracts
                .Where(c => c.DataSourceId == dsId)
                .Select(c => new
                {
                    c.SchemaName,
                    c.TableName,
                    RuleCount = c.Rules.Count
                })
                .ToListAsync(ct);

            if (contracts.Count > 0)
            {
                sb.AppendLine($"**Active Data Contracts:** {contracts.Count}");
                sb.AppendLine();
            }
        }
    }

    private async Task AppendCodeToDataLineageAsync(StringBuilder sb, List<int> dataSourceIds, CancellationToken ct)
    {
        sb.AppendLine("## Code-to-Data Lineage");
        sb.AppendLine();

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        foreach (var dsId in dataSourceIds)
        {
            var tableNames = await context.DatabaseMetadata
                .Where(m => m.DataSourceId == dsId)
                .Select(m => new { m.SchemaName, m.TableName })
                .OrderBy(m => m.SchemaName)
                .ThenBy(m => m.TableName)
                .ToListAsync(ct);

            if (tableNames.Count == 0) continue;

            var dsName = await context.DataSources
                .Where(ds => ds.Id == dsId)
                .Select(ds => ds.Name)
                .FirstOrDefaultAsync(ct) ?? $"DataSource {dsId}";

            sb.AppendLine($"### {dsName}");
            sb.AppendLine();

            foreach (var table in tableNames)
            {
                var lineage = await knowledgeGraphService.GetLineageAsync(dsId, table.SchemaName, table.TableName, ct);

                if (lineage.WrittenBy.Count == 0 && lineage.ReadBy.Count == 0 && lineage.RelatedTables.Count == 0)
                    continue;

                sb.AppendLine($"#### `{table.SchemaName}.{table.TableName}`");

                if (lineage.WrittenBy.Count > 0)
                {
                    sb.AppendLine("**Written by:**");
                    foreach (var node in lineage.WrittenBy)
                        sb.AppendLine($"- `{node.Name}`{(node.Detail != null ? $" ({node.Detail})" : "")}");
                }

                if (lineage.ReadBy.Count > 0)
                {
                    sb.AppendLine("**Read by:**");
                    foreach (var node in lineage.ReadBy)
                        sb.AppendLine($"- `{node.Name}`{(node.Detail != null ? $" ({node.Detail})" : "")}");
                }

                if (lineage.RelatedTables.Count > 0)
                {
                    sb.AppendLine("**Related tables:**");
                    foreach (var node in lineage.RelatedTables)
                        sb.AppendLine($"- `{node.Name}` [{node.Type}]{(node.Detail != null ? $" ({node.Detail})" : "")}");
                }

                sb.AppendLine();
            }
        }
    }

    private async Task AppendSchemaChangesAsync(StringBuilder sb, List<int> dataSourceIds, CancellationToken ct)
    {
        sb.AppendLine("## Schema Changes");
        sb.AppendLine();

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        foreach (var dsId in dataSourceIds)
        {
            var changes = await context.SchemaChanges
                .Where(c => c.DataSourceId == dsId)
                .OrderByDescending(c => c.DetectedAt)
                .Take(50)
                .Select(c => new
                {
                    c.DetectedAt,
                    c.ChangeType,
                    c.SchemaName,
                    c.TableName,
                    c.ColumnName,
                    c.OldValue,
                    c.NewValue,
                    c.Description
                })
                .ToListAsync(ct);

            if (changes.Count == 0) continue;

            var dsName = await context.DataSources
                .Where(ds => ds.Id == dsId)
                .Select(ds => ds.Name)
                .FirstOrDefaultAsync(ct) ?? $"DataSource {dsId}";

            sb.AppendLine($"### {dsName}");
            sb.AppendLine();
            sb.AppendLine("| Detected | Change Type | Object | Details |");
            sb.AppendLine("|----------|-------------|--------|---------|");

            foreach (var c in changes)
            {
                var obj = c.ColumnName != null
                    ? $"{c.SchemaName}.{c.TableName}.{c.ColumnName}"
                    : $"{c.SchemaName}.{c.TableName}";
                var details = c.Description ?? (c.OldValue != null && c.NewValue != null
                    ? $"{c.OldValue} → {c.NewValue}"
                    : c.NewValue ?? c.OldValue ?? string.Empty);
                sb.AppendLine($"| {c.DetectedAt:yyyy-MM-dd HH:mm} | {c.ChangeType} | `{obj}` | {details} |");
            }

            sb.AppendLine();
        }
    }

    private static void AppendRecommendations(StringBuilder sb)
    {
        sb.AppendLine("## Recommendations");
        sb.AppendLine();
        sb.AppendLine("Based on the data collected in this report, consider the following actions:");
        sb.AppendLine();
        sb.AppendLine("- Review tables with data quality scores below 80% and add or update data contracts.");
        sb.AppendLine("- Investigate recent schema changes to ensure downstream consumers have been updated.");
        sb.AppendLine("- Document tables that have no description to improve discoverability.");
        sb.AppendLine("- Verify code references are current and remove stale references to dropped columns or tables.");
        sb.AppendLine();
    }

    private static string ConvertToHtml(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        var body = Markdown.ToHtml(markdown, pipeline);

        // Use concatenation to avoid raw-string-literal conflicts with CSS brace pairs
        return "<!DOCTYPE html>\n" +
               "<html lang=\"en\">\n" +
               "<head>\n" +
               "    <meta charset=\"UTF-8\" />\n" +
               "    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />\n" +
               "    <title>Project Report</title>\n" +
               "    <style>\n" +
               "        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 1200px; margin: 0 auto; padding: 2rem; color: #1a1a1a; }\n" +
               "        h1 { border-bottom: 2px solid #0066cc; padding-bottom: 0.5rem; }\n" +
               "        h2 { border-bottom: 1px solid #ddd; padding-bottom: 0.3rem; margin-top: 2rem; }\n" +
               "        table { border-collapse: collapse; width: 100%; margin: 1rem 0; }\n" +
               "        th, td { border: 1px solid #ddd; padding: 0.5rem 0.75rem; text-align: left; }\n" +
               "        th { background-color: #f5f5f5; font-weight: 600; }\n" +
               "        tr:nth-child(even) { background-color: #fafafa; }\n" +
               "        code { background-color: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 3px; font-family: monospace; }\n" +
               "        pre { background-color: #f0f0f0; padding: 1rem; border-radius: 4px; overflow-x: auto; }\n" +
               "        blockquote { border-left: 3px solid #0066cc; margin: 0; padding: 0.5rem 1rem; color: #555; }\n" +
               "    </style>\n" +
               "</head>\n" +
               "<body>\n" +
               body +
               "\n</body>\n</html>";
    }
}
