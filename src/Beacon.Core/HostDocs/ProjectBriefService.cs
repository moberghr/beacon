using System.Text;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.HostEndpoints;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.HostDocs;

/// <summary>
/// Builds a deterministic markdown <b>project brief</b> — the text an agent workspace drops into its
/// <c>AGENTS.md</c>: what the project is, which data sources and tables matter, the glossary, the imported documents
/// and how to use Beacon's tools safely. Built from stored metadata only; no LLM call.
/// </summary>
public interface IProjectBriefService
{
    /// <summary>The brief for <paramref name="projectId"/>, or null when the project does not exist.</summary>
    Task<string?> BuildAgentsMdAsync(int projectId, CancellationToken cancellationToken);
}

internal sealed record BriefDataSource(string Name, DataSourceType Type, DatabaseEngineType? Engine, bool IsReadOnly, bool IsHostManaged, int TableCount);

internal sealed record BriefTable(string DataSourceName, string SchemaName, string TableName, string? Description, int ColumnCount);

internal sealed record BriefMaskedColumn(string SchemaName, string TableName, string ColumnName);

internal sealed record BriefGlossaryTerm(string Term, string Definition);

internal sealed record ProjectBriefData(
    string ProjectName,
    string? ProjectDescription,
    IReadOnlyList<BriefDataSource> DataSources,
    IReadOnlyList<BriefTable> Tables,
    int TotalTableCount,
    IReadOnlyList<BriefMaskedColumn> MaskedColumns,
    IReadOnlyList<BriefGlossaryTerm> GlossaryTerms,
    int TotalGlossaryTermCount,
    IReadOnlyList<ImportedDocumentSummary> Documents,
    int TotalDocumentCount,
    HostEndpointToolListing? EndpointTools = null);

internal sealed class ProjectBriefService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEnumerable<IHostEndpointToolCatalog>? endpointToolCatalogs = null) : IProjectBriefService
{
    public const int MaxTables = 30;
    public const int MaxGlossaryTerms = 30;
    public const int MaxMaskedColumns = 50;
    public const int MaxDocuments = 100;

    public async Task<string?> BuildAgentsMdAsync(int projectId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var project = await context.Projects
            .Where(x => x.Id == projectId)
            .Select(x =>
                new
                {
                    x.Name,
                    x.Description
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
        {
            return null;
        }

        var dataSourceIds = await context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.DataSourceId)
            .ToListAsync(cancellationToken);

        var dataSources = await BuildDataSourcesQuery(context, projectId)
            .ToListAsync(cancellationToken);

        var tables = await BuildTopTablesQuery(context, dataSourceIds, MaxTables)
            .ToListAsync(cancellationToken);

        var totalTables = await context.DatabaseMetadata
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .CountAsync(cancellationToken);

        var maskedColumns = await BuildMaskedColumnsQuery(context, dataSourceIds, MaxMaskedColumns)
            .ToListAsync(cancellationToken);

        var glossary = await BuildGlossaryQuery(context, projectId, MaxGlossaryTerms)
            .ToListAsync(cancellationToken);

        var totalGlossary = await context.McpGlossaryTerms
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.IsActive)
            .CountAsync(cancellationToken);

        var documents = await ImportedDocumentQueries.ListForProject(context, projectId)
            .Take(MaxDocuments)
            .ToListAsync(cancellationToken);

        var totalDocuments = await context.ProjectImportedDocuments
            .Where(x => x.ProjectId == projectId)
            .CountAsync(cancellationToken);

        var data = new ProjectBriefData(
            project.Name,
            project.Description,
            dataSources,
            tables,
            totalTables,
            maskedColumns,
            glossary,
            totalGlossary,
            documents,
            totalDocuments,
            await GetEndpointToolsAsync(projectId, cancellationToken));

        return Render(data);
    }

    internal static IQueryable<BriefDataSource> BuildDataSourcesQuery(BeaconContext context, int projectId)
    {
        return context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.DataSource.Name)
            .ThenBy(x => x.DataSourceId)
            .Select(x =>
                new BriefDataSource(
                    x.DataSource.Name,
                    x.DataSource.DataSourceType,
                    x.DataSource.DatabaseEngineType,
                    x.DataSource.IsReadOnly,
                    x.DataSource.HostManagedKey != null,
                    context.DatabaseMetadata.Count(y => y.DataSourceId == x.DataSourceId)));
    }

    // Most-described first (a table description, then described columns), then most-related (foreign-key
    // columns), then a stable name order so the brief is byte-identical between calls.
    internal static IQueryable<BriefTable> BuildTopTablesQuery(BeaconContext context, List<int> dataSourceIds, int take)
    {
        return context.DatabaseMetadata
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .OrderByDescending(x => x.TableDescription != null && x.TableDescription != "")
            .ThenByDescending(x => x.Columns.Count(y => y.Description != null && y.Description != ""))
            .ThenByDescending(x => x.Columns.Count(y => y.IsForeignKey))
            .ThenBy(x => x.SchemaName)
            .ThenBy(x => x.TableName)
            .ThenBy(x => x.Id)
            .Select(x =>
                new BriefTable(x.DataSource.Name, x.SchemaName, x.TableName, x.TableDescription, x.Columns.Count))
            .Take(take);
    }

    internal static IQueryable<BriefMaskedColumn> BuildMaskedColumnsQuery(BeaconContext context, List<int> dataSourceIds, int take)
    {
        return context.ColumnMetadata
            .Where(x => dataSourceIds.Contains(x.DatabaseMetadata.DataSourceId))
            .Where(x => x.Description != null)
            .Where(x => x.Description!.Contains(HostModelReader.MaskedDescriptionMarker))
            .OrderBy(x => x.DatabaseMetadata.SchemaName)
            .ThenBy(x => x.DatabaseMetadata.TableName)
            .ThenBy(x => x.ColumnName)
            .Select(x =>
                new BriefMaskedColumn(x.DatabaseMetadata.SchemaName, x.DatabaseMetadata.TableName, x.ColumnName))
            .Take(take);
    }

    private async Task<HostEndpointToolListing?> GetEndpointToolsAsync(int projectId, CancellationToken cancellationToken)
    {
        foreach (var catalog in endpointToolCatalogs ?? [])
        {
            var listing = await catalog.GetForProjectAsync(projectId, cancellationToken);
            if (listing is { Tools.Count: > 0 })
            {
                return listing;
            }
        }

        return null;
    }

    internal static IQueryable<BriefGlossaryTerm> BuildGlossaryQuery(BeaconContext context, int projectId, int take)
    {
        return context.McpGlossaryTerms
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.IsActive)
            .OrderBy(x => x.Term)
            .ThenBy(x => x.Id)
            .Select(x =>
                new BriefGlossaryTerm(x.Term, x.Definition))
            .Take(take);
    }

    internal static string Render(ProjectBriefData data)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(data.ProjectName).Append(" — Beacon project brief\n\n");
        sb.Append("> Generated by Beacon `get_context` (format `agents_md`) from the project's catalog. Regenerate it when the project changes.\n\n");

        if (!string.IsNullOrWhiteSpace(data.ProjectDescription))
        {
            sb.Append(data.ProjectDescription.Trim()).Append("\n\n");
        }

        AppendTools(sb, data);
        AppendEndpointTools(sb, data);
        AppendDataSources(sb, data);
        AppendTables(sb, data);
        AppendMaskedColumns(sb, data);
        AppendGlossary(sb, data);
        AppendDocuments(sb, data);

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendTools(StringBuilder sb, ProjectBriefData data)
    {
        sb.Append("## Working with this project through Beacon\n\n");
        sb.Append("Beacon exposes this project over MCP. Access is **read-only**: only SELECT / WITH statements run, and write statements are rejected at several layers — do not attempt them.\n\n");
        sb.Append("For hand-written SQL use `get_query_context` → `dry_run` → `query`:\n\n");
        sb.Append("1. `search` — find tables, columns and documents by keyword.\n");
        sb.Append("2. `get_query_context` — grounding for one question: schemas with sample values, verified join paths, example queries, glossary.\n");
        sb.Append("3. `dry_run` — validate the SQL through every safety gate without executing it.\n");
        sb.Append("4. `query` — run the validated SQL; results are row-capped and masked columns come back masked.\n\n");
        sb.Append("Also available: `get_context` (project overview), `get_documentation` (documentation, table detail");
        sb.Append(data.TotalDocumentCount > 0 ? ", and the documents below via `document: \"<path>\"`)" : ")");
        sb.Append(", `ask` (natural-language question, when AI is enabled on the Beacon host) and `feedback` (report whether an `ask` answer was correct).\n\n");
        sb.Append("Rules:\n\n");
        sb.Append("- Masked columns stay masked: never try to unmask, reconstruct or infer their values.\n");
        sb.Append("- Excluded tables and columns are not visible to Beacon; if something is missing, it is out of scope, not hidden.\n");
        sb.Append("- Pass `project_id` on every call when your credentials reach more than one project.\n\n");
    }

    private static void AppendEndpointTools(StringBuilder sb, ProjectBriefData data)
    {
        if (data.EndpointTools is not { Tools.Count: > 0 } listing)
        {
            return;
        }

        sb.Append("## Host application tools\n\n");
        sb.Append("Read-only endpoints of the host application, run as your host identity: the host's own permissions decide what you may see. ");
        sb.Append(listing.CatalogMode
            ? "Find one with `search_api` (it returns the input schema) and run it with `call_api`.\n\n"
            : "Call them by name.\n\n");

        foreach (var tool in listing.Tools)
        {
            var name = listing.CatalogMode ? tool.Name : tool.ToolName;
            sb.Append("- `").Append(name).Append('`');
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                sb.Append(" — ").Append(tool.Description.Trim());
            }

            sb.Append('\n');
        }

        sb.Append('\n');
    }

    private static void AppendDataSources(StringBuilder sb, ProjectBriefData data)
    {
        sb.Append("## Data sources\n\n");
        if (data.DataSources.Count == 0)
        {
            sb.Append("No data sources are linked to this project.\n\n");
            return;
        }

        sb.Append("| Data source | Engine | Tables | Access |\n");
        sb.Append("| --- | --- | --- | --- |\n");
        foreach (var dataSource in data.DataSources)
        {
            var engine = dataSource.Engine?.ToString() ?? dataSource.Type.ToString();
            var access = dataSource.IsHostManaged
                ? "host-managed, read-only (an allow-listed slice of the host application's model)"
                : dataSource.IsReadOnly ? "read-only" : "read-only through Beacon";

            sb.Append("| ").Append(Cell(dataSource.Name))
                .Append(" | ").Append(engine)
                .Append(" | ").Append(dataSource.TableCount)
                .Append(" | ").Append(access)
                .Append(" |\n");
        }

        sb.Append('\n');
    }

    private static void AppendTables(StringBuilder sb, ProjectBriefData data)
    {
        if (data.Tables.Count == 0)
        {
            return;
        }

        sb.Append("## Key tables\n\n");
        if (data.TotalTableCount > data.Tables.Count)
        {
            sb.Append("The ").Append(data.Tables.Count).Append(" best-described of ").Append(data.TotalTableCount)
                .Append(" tables; use `search` for the rest.\n\n");
        }

        sb.Append("| Table | Data source | Columns | Description |\n");
        sb.Append("| --- | --- | --- | --- |\n");
        foreach (var table in data.Tables)
        {
            sb.Append("| `").Append(Cell(table.SchemaName)).Append('.').Append(Cell(table.TableName)).Append('`')
                .Append(" | ").Append(Cell(table.DataSourceName))
                .Append(" | ").Append(table.ColumnCount)
                .Append(" | ").Append(Cell(Truncate(table.Description, 200)))
                .Append(" |\n");
        }

        sb.Append('\n');
    }

    private static void AppendMaskedColumns(StringBuilder sb, ProjectBriefData data)
    {
        if (data.MaskedColumns.Count == 0)
        {
            return;
        }

        sb.Append("## Masked columns\n\n");
        sb.Append("Values of these columns are masked in every query result:\n\n");
        foreach (var column in data.MaskedColumns)
        {
            sb.Append("- `").Append(column.SchemaName).Append('.').Append(column.TableName).Append('.').Append(column.ColumnName).Append("`\n");
        }

        sb.Append('\n');
    }

    private static void AppendGlossary(StringBuilder sb, ProjectBriefData data)
    {
        if (data.GlossaryTerms.Count == 0)
        {
            return;
        }

        sb.Append("## Glossary\n\n");
        foreach (var term in data.GlossaryTerms)
        {
            sb.Append("- **").Append(OneLine(term.Term)).Append("** — ").Append(OneLine(Truncate(term.Definition, 300))).Append('\n');
        }

        if (data.TotalGlossaryTermCount > data.GlossaryTerms.Count)
        {
            sb.Append("- … ").Append(data.TotalGlossaryTermCount - data.GlossaryTerms.Count).Append(" more; `get_query_context` returns the relevant terms per question.\n");
        }

        sb.Append('\n');
    }

    private static void AppendDocuments(StringBuilder sb, ProjectBriefData data)
    {
        if (data.Documents.Count == 0)
        {
            return;
        }

        sb.Append("## Documents\n\n");
        sb.Append("Fetch one with `get_documentation` and `document: \"<path>\"`; `search` also matches their text.\n\n");
        foreach (var document in data.Documents)
        {
            sb.Append("- ").Append(OneLine(document.Title)).Append(" — `").Append(document.Path).Append("`\n");
        }

        if (data.TotalDocumentCount > data.Documents.Count)
        {
            sb.Append("- … ").Append(data.TotalDocumentCount - data.Documents.Count).Append(" more; `get_documentation` lists them all.\n");
        }

        sb.Append('\n');
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max].TrimEnd() + "…";
    }

    private static string OneLine(string value)
    {
        return value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string Cell(string value)
    {
        return OneLine(value).Replace("|", "\\|");
    }
}
