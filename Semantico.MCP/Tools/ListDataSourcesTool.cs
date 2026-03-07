using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class ListDataSourcesTool(IDbContextFactory<SemanticoContext> contextFactory) : IMcpTool
{
    public string Name => "list_datasources";
    public string Description => "List all data sources, or list all tables in a specific data source grouped by schema.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["datasource_id"] = ToolHelper.IntProp("Optional. If provided, lists all tables in this data source grouped by schema.")
        });

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var dsId = ToolHelper.GetInt(arguments, "datasource_id");
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        if (dsId != null)
        {
            var scopeError = ToolHelper.ValidateDataSourceAccess(session, dsId.Value);
            if (scopeError != null) return scopeError;
            return await ListTablesAsync(context, dsId.Value, ct);
        }

        return await ListDataSourcesAsync(context, session.AllowedDataSourceIds, ct);
    }

    private static async Task<McpToolResult> ListDataSourcesAsync(SemanticoContext context, List<int>? allowedIds, CancellationToken ct)
    {
        var query = context.DataSources.AsQueryable();
        if (allowedIds != null)
            query = query.Where(ds => allowedIds.Contains(ds.Id));

        var dataSources = await query
            .Select(ds => new
            {
                ds.Id,
                ds.Name,
                Type = ds.DataSourceType.ToString(),
                Engine = ds.DatabaseEngineType != null ? ds.DatabaseEngineType.ToString() : null,
                TableCount = context.DatabaseMetadata.Count(m => m.DataSourceId == ds.Id)
            })
            .ToListAsync(ct);

        var text = "# Available Data Sources\n\n";
        foreach (var ds in dataSources)
        {
            text += $"- **{ds.Name}** (ID: {ds.Id})\n";
            text += $"  Type: {ds.Type}";
            if (ds.Engine != null) text += $", Engine: {ds.Engine}";
            text += $", Tables: {ds.TableCount}\n";
        }

        return ToolHelper.TextResult(text);
    }

    private static async Task<McpToolResult> ListTablesAsync(SemanticoContext context, int dataSourceId, CancellationToken ct)
    {
        var dataSource = await context.DataSources
            .Where(ds => ds.Id == dataSourceId)
            .Select(ds => new { ds.Id, ds.Name })
            .FirstOrDefaultAsync(ct);

        if (dataSource == null)
            return ToolHelper.ErrorResult($"Data source {dataSourceId} not found.");

        var tables = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .Select(m => new
            {
                m.SchemaName,
                m.TableName,
                ColumnCount = m.Columns.Count
            })
            .OrderBy(t => t.SchemaName).ThenBy(t => t.TableName)
            .ToListAsync(ct);

        var text = $"# Tables in {dataSource.Name} (ID: {dataSource.Id})\n\n";

        var grouped = tables.GroupBy(t => t.SchemaName ?? "(default)");
        foreach (var schema in grouped)
        {
            text += $"## Schema: {schema.Key}\n\n";
            foreach (var table in schema)
                text += $"- **{table.TableName}** ({table.ColumnCount} columns)\n";
            text += "\n";
        }

        text += $"**Total:** {tables.Count} tables\n";
        return ToolHelper.TextResult(text);
    }
}
