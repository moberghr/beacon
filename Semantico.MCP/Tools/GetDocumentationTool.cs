using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.AI.Services.Knowledge;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class GetDocumentationTool(
    IKnowledgeGraphService knowledgeGraph,
    IDbContextFactory<SemanticoContext> contextFactory) : IMcpTool
{
    public string Name => "get_documentation";
    public string Description => "Get documentation for a data source or a specific table, including schema details, AI-generated descriptions, relationships, code references, quality scores, and lineage.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["datasource_id"] = ToolHelper.IntProp("The data source ID"),
            ["table_name"] = ToolHelper.StringProp("Optional. Table name to get detailed table documentation."),
            ["schema_name"] = ToolHelper.StringProp("Optional. Schema name to narrow scope (used with table_name).")
        },
        ["datasource_id"]);

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var dsId = ToolHelper.GetInt(arguments, "datasource_id");
        var tableName = ToolHelper.GetString(arguments, "table_name");
        var schemaName = ToolHelper.GetString(arguments, "schema_name");

        if (dsId == null)
            return ToolHelper.ErrorResult("Missing required parameter: datasource_id");

        var scopeError = ToolHelper.ValidateDataSourceAccess(session, dsId.Value);
        if (scopeError != null) return scopeError;

        try
        {
            if (!string.IsNullOrEmpty(tableName))
            {
                if (string.IsNullOrEmpty(schemaName))
                    schemaName = await ResolveDefaultSchemaAsync(dsId.Value, ct);
                return await GetTableDocumentationAsync(dsId.Value, schemaName, tableName, ct);
            }

            return await GetDataSourceDocumentationAsync(dsId.Value, ct);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error: {ex.Message}");
        }
    }

    private async Task<string> ResolveDefaultSchemaAsync(int dataSourceId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var engineType = await context.DataSources
            .Where(ds => ds.Id == dataSourceId)
            .Select(ds => ds.DatabaseEngineType)
            .FirstOrDefaultAsync(ct);

        return engineType switch
        {
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse => "dbo",
            _ => "public"
        };
    }

    private async Task<McpToolResult> GetTableDocumentationAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct)
    {
        var knowledge = await knowledgeGraph.GetTableKnowledgeAsync(dataSourceId, schemaName, tableName, ct);
        var lineage = await knowledgeGraph.GetLineageAsync(dataSourceId, schemaName, tableName, ct);

        var text = $"# {knowledge.SchemaName}.{knowledge.TableName}\n";
        text += $"**Data Source:** {knowledge.DataSourceName} (ID: {knowledge.DataSourceId})\n\n";

        if (!string.IsNullOrEmpty(knowledge.Description))
            text += $"## Description\n{knowledge.Description}\n\n";

        if (!string.IsNullOrEmpty(knowledge.BusinessPurpose))
            text += $"**Business Purpose:** {knowledge.BusinessPurpose}\n\n";

        // Columns
        if (knowledge.Columns.Count > 0)
        {
            text += "## Columns\n\n";
            text += "| Column | Type | Nullable | PK | Description |\n";
            text += "| --- | --- | --- | --- | --- |\n";
            foreach (var col in knowledge.Columns)
            {
                var pk = col.IsPrimaryKey ? "Yes" : "";
                var nullable = col.IsNullable ? "Yes" : "No";
                var fk = col.ForeignKeyTable != null ? $" → {col.ForeignKeyTable}.{col.ForeignKeyColumn}" : "";
                text += $"| {col.Name} | {col.DataType} | {nullable} | {pk} | {col.Description ?? ""}{fk} |\n";
            }
            text += "\n";
        }

        // Relationships
        if (knowledge.Relationships.Count > 0)
        {
            text += "## Relationships\n\n";
            foreach (var rel in knowledge.Relationships)
                text += $"- {rel.Type}: {rel.RelatedSchema}.{rel.RelatedTable} ({rel.ForeignKeyColumn} → {rel.ReferencedColumn})\n";
            text += "\n";
        }

        // Quality
        if (knowledge.QualityScore != null)
        {
            text += $"## Data Quality\n\n**Score:** {knowledge.QualityScore:F1}%";
            if (knowledge.QualityTrend != null) text += $" (Trend: {knowledge.QualityTrend})";
            text += "\n\n";

            if (knowledge.QualityRules.Count > 0)
            {
                foreach (var rule in knowledge.QualityRules)
                {
                    var status = rule.Passed ? "PASS" : "FAIL";
                    var col = rule.ColumnName != null ? $" [{rule.ColumnName}]" : "";
                    text += $"- {status}: {rule.RuleType}{col}";
                    if (rule.Details != null) text += $" — {rule.Details}";
                    text += "\n";
                }
                text += "\n";
            }
        }

        // Code references
        if (knowledge.CodeReferences.Count > 0)
        {
            text += "## Code References\n\n";
            foreach (var cr in knowledge.CodeReferences)
            {
                var location = cr.ClassName != null ? $"{cr.ClassName}.{cr.MethodName}" : cr.FilePath;
                text += $"- {cr.Type}: `{location}` ({cr.FilePath}:{cr.LineNumber})\n";
            }
            text += "\n";
        }

        // Lineage
        if (lineage.WrittenBy.Count > 0 || lineage.ReadBy.Count > 0 || lineage.RelatedTables.Count > 0)
        {
            text += "## Lineage\n\n";
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
        }

        return ToolHelper.TextResult(text);
    }

    private async Task<McpToolResult> GetDataSourceDocumentationAsync(int dataSourceId, CancellationToken ct)
    {
        var knowledge = await knowledgeGraph.GetDataSourceKnowledgeAsync(dataSourceId, ct);
        var llmContext = await knowledgeGraph.GetContextForLlmAsync(dataSourceId, ct: ct);

        var text = $"# {knowledge.Name}\n\n";

        if (knowledge.DatabaseEngine != null) text += $"**Engine:** {knowledge.DatabaseEngine}\n";
        text += $"**Tables:** {knowledge.TableCount}\n";
        text += $"**Code References:** {knowledge.CodeReferenceCount}\n";
        text += $"**Documentation:** {(knowledge.HasDocumentation ? "Yes" : "Not yet generated")}\n";
        if (knowledge.OverallQualityScore != null)
            text += $"**Overall Quality Score:** {knowledge.OverallQualityScore:F1}%\n";
        text += "\n";

        // Schemas
        if (knowledge.Schemas.Count > 0)
        {
            text += "## Schemas\n\n";
            foreach (var schema in knowledge.Schemas)
            {
                text += $"- **{schema.SchemaName}**: {schema.TableCount} tables";
                if (schema.AvgQualityScore != null) text += $", avg quality: {schema.AvgQualityScore:F1}%";
                text += "\n";
            }
            text += "\n";
        }

        // LLM context (includes table list with descriptions)
        if (!string.IsNullOrEmpty(llmContext))
        {
            text += "## Detailed Context\n\n";
            text += llmContext + "\n";
        }

        return ToolHelper.TextResult(text);
    }
}
