using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Documentation;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class ProjectGetDocumentationTool(
    IKnowledgeGraphService knowledgeGraph,
    IProjectDocumentationService documentationService,
    IDbContextFactory<SemanticoContext> contextFactory) : IMcpTool
{
    public string Name => "get_documentation";
    public string Description => "Get AI-generated documentation for the project, a specific data source, or a specific table/endpoint. Includes schema details, relationships, code references, quality scores, and lineage.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["project_id"] = ToolHelper.IntProp("Optional. Specify project if your API key has access to multiple projects."),
            ["datasource_name"] = ToolHelper.StringProp("Optional. Get documentation for a specific data source by name."),
            ["table_name"] = ToolHelper.StringProp("Optional. Table name or API endpoint to get detailed documentation."),
            ["schema_name"] = ToolHelper.StringProp("Optional. Schema name or API tag to narrow scope.")
        });

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var requestedProjectId = ToolHelper.GetInt(arguments, "project_id");
        var resolveError = ToolHelper.ResolveProjectId(session, requestedProjectId, out var projectId);
        if (resolveError != null) return resolveError;

        var dsName = ToolHelper.GetString(arguments, "datasource_name");
        var tableName = ToolHelper.GetString(arguments, "table_name");
        var schemaName = ToolHelper.GetString(arguments, "schema_name");

        try
        {
            // If no data source specified, return full project documentation
            if (string.IsNullOrEmpty(dsName) && string.IsNullOrEmpty(tableName))
                return await GetProjectDocumentationAsync(projectId, ct);

            // Resolve data source
            int? dsId = null;
            if (!string.IsNullOrEmpty(dsName))
            {
                var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, dsName, ct);
                if (nameError != null) return nameError;
                dsId = resolvedId;
            }

            // If we have a table name but no data source, try to find which data source has that table
            if (dsId == null && !string.IsNullOrEmpty(tableName))
            {
                dsId = await FindDataSourceForTableAsync(projectId, tableName, schemaName, ct);
                if (dsId == null)
                    return ToolHelper.ErrorResult($"Could not find table '{tableName}' in any data source of this project.");
            }

            if (dsId == null)
                return ToolHelper.ErrorResult("Could not resolve data source.");

            if (!string.IsNullOrEmpty(tableName))
            {
                var (resolvedSchema, isApi) = await ResolveSchemaAndTypeAsync(dsId.Value, ct);
                if (string.IsNullOrEmpty(schemaName))
                    schemaName = resolvedSchema;
                return await GetTableDocumentationAsync(dsId.Value, schemaName, tableName, isApi, ct);
            }

            return await GetDataSourceDocumentationAsync(dsId.Value, ct);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error: {ex.Message}");
        }
    }

    private async Task<int?> FindDataSourceForTableAsync(int projectId, string tableName, string? schemaName, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var projectDsIds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        var query = context.DatabaseMetadata
            .Where(m => projectDsIds.Contains(m.DataSourceId))
            .Where(m => m.TableName.ToLower() == tableName.ToLower());

        if (!string.IsNullOrEmpty(schemaName))
            query = query.Where(m => m.SchemaName == schemaName);

        return await query.Select(m => (int?)m.DataSourceId).FirstOrDefaultAsync(ct);
    }

    private async Task<(string Schema, bool IsApi)> ResolveSchemaAndTypeAsync(int dataSourceId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var ds = await context.DataSources
            .Where(d => d.Id == dataSourceId)
            .Select(d => new { d.DataSourceType, d.DatabaseEngineType })
            .FirstOrDefaultAsync(ct);

        var isApi = ds?.DataSourceType == DataSourceType.Api;
        var schema = isApi ? "default" : ds?.DatabaseEngineType switch
        {
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse => "dbo",
            _ => "public"
        };

        return (schema, isApi);
    }

    private async Task<McpToolResult> GetProjectDocumentationAsync(int projectId, CancellationToken ct)
    {
        var markdown = await documentationService.ExportLatestToMarkdownAsync(projectId, ct);
        return markdown != null
            ? ToolHelper.TextResult(markdown)
            : ToolHelper.TextResult($"No documentation has been generated for this project yet. Use the Semantico UI to generate project documentation.");
    }

    private async Task<McpToolResult> GetTableDocumentationAsync(int dataSourceId, string schemaName, string tableName, bool isApi, CancellationToken ct)
    {
        var knowledgeTask = knowledgeGraph.GetTableKnowledgeAsync(dataSourceId, schemaName, tableName, ct);
        var lineageTask = knowledgeGraph.GetLineageAsync(dataSourceId, schemaName, tableName, ct);
        await Task.WhenAll(knowledgeTask, lineageTask);

        var knowledge = knowledgeTask.Result;
        var lineage = lineageTask.Result;

        string text;
        if (isApi)
            text = FormatApiEndpointDocumentation(knowledge, lineage);
        else
            text = FormatTableDocumentation(knowledge, lineage);

        return ToolHelper.TextResult(text);
    }

    private static string FormatApiEndpointDocumentation(TableKnowledge knowledge, LineageInfo lineage)
    {
        var text = $"# {knowledge.TableName}\n";
        text += $"**API:** {knowledge.DataSourceName} (ID: {knowledge.DataSourceId})\n";
        text += $"**Tag:** {knowledge.SchemaName}\n\n";

        if (!string.IsNullOrEmpty(knowledge.Description))
            text += $"## Description\n{knowledge.Description}\n\n";

        if (!string.IsNullOrEmpty(knowledge.BusinessPurpose))
            text += $"**Purpose:** {knowledge.BusinessPurpose}\n\n";

        if (knowledge.Columns.Count > 0)
        {
            text += "## Response Fields\n\n";
            text += "| Field | Type | Description |\n";
            text += "| --- | --- | --- |\n";
            foreach (var col in knowledge.Columns)
                text += $"| {col.Name} | {col.DataType} | {col.Description ?? ""} |\n";
            text += "\n";
        }

        text += FormatQualitySection(knowledge);
        text += FormatCodeReferencesSection(knowledge);
        text += FormatLineageSection(lineage);

        return text;
    }

    private static string FormatTableDocumentation(TableKnowledge knowledge, LineageInfo lineage)
    {
        var text = $"# {knowledge.SchemaName}.{knowledge.TableName}\n";
        text += $"**Data Source:** {knowledge.DataSourceName} (ID: {knowledge.DataSourceId})\n\n";

        if (!string.IsNullOrEmpty(knowledge.Description))
            text += $"## Description\n{knowledge.Description}\n\n";

        if (!string.IsNullOrEmpty(knowledge.BusinessPurpose))
            text += $"**Business Purpose:** {knowledge.BusinessPurpose}\n\n";

        if (knowledge.Columns.Count > 0)
        {
            text += "## Columns\n\n";
            text += "| Column | Type | Nullable | PK | Description |\n";
            text += "| --- | --- | --- | --- | --- |\n";
            foreach (var col in knowledge.Columns)
            {
                var pk = col.IsPrimaryKey ? "Yes" : "";
                var nullable = col.IsNullable ? "Yes" : "No";
                var fk = col.ForeignKeyTable != null ? $" -> {col.ForeignKeyTable}.{col.ForeignKeyColumn}" : "";
                text += $"| {col.Name} | {col.DataType} | {nullable} | {pk} | {col.Description ?? ""}{fk} |\n";
            }
            text += "\n";
        }

        if (knowledge.Relationships.Count > 0)
        {
            text += "## Relationships\n\n";
            foreach (var rel in knowledge.Relationships)
                text += $"- {rel.Type}: {rel.RelatedSchema}.{rel.RelatedTable} ({rel.ForeignKeyColumn} -> {rel.ReferencedColumn})\n";
            text += "\n";
        }

        text += FormatQualitySection(knowledge);
        text += FormatCodeReferencesSection(knowledge);
        text += FormatLineageSection(lineage);

        return text;
    }

    private static string FormatQualitySection(TableKnowledge knowledge)
    {
        if (knowledge.QualityScore == null) return "";

        var text = $"## Data Quality\n\n**Score:** {knowledge.QualityScore:F1}%";
        if (knowledge.QualityTrend != null) text += $" (Trend: {knowledge.QualityTrend})";
        text += "\n\n";

        if (knowledge.QualityRules.Count > 0)
        {
            foreach (var rule in knowledge.QualityRules)
            {
                var status = rule.Passed ? "PASS" : "FAIL";
                var col = rule.ColumnName != null ? $" [{rule.ColumnName}]" : "";
                text += $"- {status}: {rule.RuleType}{col}";
                if (rule.Details != null) text += $" -- {rule.Details}";
                text += "\n";
            }
            text += "\n";
        }

        return text;
    }

    private static string FormatCodeReferencesSection(TableKnowledge knowledge)
    {
        if (knowledge.CodeReferences.Count == 0) return "";

        var text = "## Code References\n\n";
        foreach (var cr in knowledge.CodeReferences)
        {
            var location = cr.ClassName != null ? $"{cr.ClassName}.{cr.MethodName}" : cr.FilePath;
            text += $"- {cr.Type}: `{location}` ({cr.FilePath}:{cr.LineNumber})\n";
        }
        text += "\n";
        return text;
    }

    private static string FormatLineageSection(LineageInfo lineage)
    {
        if (lineage.WrittenBy.Count == 0 && lineage.ReadBy.Count == 0 && lineage.RelatedTables.Count == 0)
            return "";

        var text = "## Lineage\n\n";
        if (lineage.WrittenBy.Count > 0)
        {
            text += "**Written by:**\n";
            foreach (var node in lineage.WrittenBy)
                text += $"- {node.Type}: {node.Name}" + (node.Detail != null ? $" ({node.Detail})" : "") + "\n";
        }
        if (lineage.ReadBy.Count > 0)
        {
            text += "**Read by:**\n";
            foreach (var node in lineage.ReadBy)
                text += $"- {node.Type}: {node.Name}" + (node.Detail != null ? $" ({node.Detail})" : "") + "\n";
        }
        if (lineage.RelatedTables.Count > 0)
        {
            text += "**Related tables:**\n";
            foreach (var node in lineage.RelatedTables)
                text += $"- {node.Name}" + (node.Detail != null ? $" ({node.Detail})" : "") + "\n";
        }
        return text;
    }

    private async Task<McpToolResult> GetDataSourceDocumentationAsync(int dataSourceId, CancellationToken ct)
    {
        var knowledgeTask = knowledgeGraph.GetDataSourceKnowledgeAsync(dataSourceId, ct);
        var llmContextTask = knowledgeGraph.GetContextForLlmAsync(dataSourceId, ct: ct);
        await Task.WhenAll(knowledgeTask, llmContextTask);

        var knowledge = knowledgeTask.Result;
        var llmContext = llmContextTask.Result;
        var isApi = knowledge.DataSourceType == DataSourceType.Api;

        var text = $"# {knowledge.Name}\n\n";

        if (knowledge.DatabaseEngine != null) text += $"**{(isApi ? "Type" : "Engine")}:** {knowledge.DatabaseEngine}\n";
        text += $"**{(isApi ? "Endpoints" : "Tables")}:** {knowledge.TableCount}\n";
        text += $"**Code References:** {knowledge.CodeReferenceCount}\n";
        text += $"**Documentation:** {(knowledge.HasDocumentation ? "Yes" : "Not yet generated")}\n";
        if (knowledge.OverallQualityScore != null)
            text += $"**Overall Quality Score:** {knowledge.OverallQualityScore:F1}%\n";
        text += "\n";

        if (knowledge.Schemas.Count > 0)
        {
            text += isApi ? "## Tags\n\n" : "## Schemas\n\n";
            foreach (var schema in knowledge.Schemas)
            {
                text += $"- **{schema.SchemaName}**: {schema.TableCount} {(isApi ? "endpoints" : "tables")}";
                if (schema.AvgQualityScore != null) text += $", avg quality: {schema.AvgQualityScore:F1}%";
                text += "\n";
            }
            text += "\n";
        }

        if (!string.IsNullOrEmpty(llmContext))
        {
            text += "## Detailed Context\n\n";
            text += llmContext + "\n";
        }

        return ToolHelper.TextResult(text);
    }
}
