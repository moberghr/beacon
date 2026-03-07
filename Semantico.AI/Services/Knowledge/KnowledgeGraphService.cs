using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using System.Text;

namespace Semantico.AI.Services.Knowledge;

internal sealed class KnowledgeGraphService(
    IDbContextFactory<SemanticoContext> contextFactory,
    ILogger<KnowledgeGraphService> logger) : IKnowledgeGraphService
{
    public async Task<TableKnowledge> GetTableKnowledgeAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var dataSource = await context.DataSources
            .Where(ds => ds.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        // Get schema metadata
        var metadata = await context.DatabaseMetadata
            .Include(m => m.Columns)
            .Include(m => m.Indexes)
            .Where(m => m.DataSourceId == dataSourceId && m.SchemaName == schemaName && m.TableName == tableName)
            .FirstOrDefaultAsync(ct);

        // Get documentation - use GetDisplayContent() pattern; DocumentationSection has no SchemaName
        var docSection = await context.DocumentationSections
            .Where(d => d.Documentation.DataSourceId == dataSourceId && d.TableName == tableName)
            .FirstOrDefaultAsync(ct);

        // Get code references (from GitHub scanner)
        var codeRefs = await context.CodeReferences
            .Where(r => r.GitHubRepository.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId))
            .Where(r => r.TableName != null && r.TableName.ToLower() == tableName.ToLower())
            .Take(50)
            .ToListAsync(ct);

        // Get data quality score - property is Score, not OverallScore
        var qualityScore = await context.DataQualityScores
            .Where(q => q.DataSourceId == dataSourceId && q.SchemaName == schemaName && q.TableName == tableName)
            .FirstOrDefaultAsync(ct);

        var qualityRules = new List<QualityRuleInfo>();
        var contract = await context.DataContracts
            .Include(c => c.Rules)
            .Where(c => c.DataSourceId == dataSourceId && c.SchemaName == schemaName && c.TableName == tableName)
            .FirstOrDefaultAsync(ct);

        if (contract != null)
        {
            // Get latest evaluation results - EvaluatedAt does not exist; use CreatedTime from BaseEntity
            var latestEval = await context.DataQualityEvaluations
                .Include(e => e.RuleResults)
                .Where(e => e.DataContractId == contract.Id)
                .OrderByDescending(e => e.CreatedTime)
                .FirstOrDefaultAsync(ct);

            if (latestEval != null)
            {
                foreach (var rule in contract.Rules)
                {
                    var result = latestEval.RuleResults.FirstOrDefault(r => r.DataContractRuleId == rule.Id);
                    // RuleResult error detail is in Message property, not ErrorMessage
                    qualityRules.Add(new QualityRuleInfo(
                        rule.RuleType.ToString(),
                        rule.ColumnName,
                        result?.Passed ?? false,
                        result?.Message
                    ));
                }
            }
        }

        // Count query executions that touched this data source via query steps
        var queryCount = await context.QueryExecutionHistory
            .Where(h => h.Subscription.Query.Steps.Any(s => s.DataSourceId == dataSourceId))
            .CountAsync(ct);

        // Build columns
        var columns = metadata?.Columns.Select(c => new ColumnInfo(
            c.ColumnName, c.DataType, c.IsNullable, c.IsPrimaryKey,
            c.Description, c.ForeignKeyTable, c.ForeignKeyColumn
        )).ToList() ?? new List<ColumnInfo>();

        // Build indexes - Columns is string[], join to a single display string
        var indexes = metadata?.Indexes.Select(i => new IndexInfo(
            i.IndexName, string.Join(", ", i.Columns), i.IsUnique
        )).ToList() ?? new List<IndexInfo>();

        // Build relationships from FK columns
        var relationships = columns
            .Where(c => c.ForeignKeyTable != null)
            .Select(c => new RelationshipInfo("ForeignKey", schemaName, c.ForeignKeyTable!, c.Name, c.ForeignKeyColumn!))
            .ToList();

        return new TableKnowledge
        {
            DataSourceId = dataSourceId,
            DataSourceName = dataSource.Name,
            SchemaName = schemaName,
            TableName = tableName,
            Columns = columns,
            Indexes = indexes,
            Relationships = relationships,
            // DocumentationSection uses GetDisplayContent() to return the active content
            Description = docSection?.GetDisplayContent(),
            BusinessPurpose = metadata?.TableDescription,
            CodeReferences = codeRefs.Select(r => new CodeReferenceInfo(
                r.FilePath, r.LineNumber, r.ReferenceType, r.ClassName, r.MethodName, r.CodeSnippet
            )).ToList(),
            // DataQualityScore property is Score, not OverallScore
            QualityScore = qualityScore?.Score,
            QualityTrend = qualityScore?.TrendDirection.ToString(),
            QualityRules = qualityRules,
            QueryCount = queryCount
        };
    }

    public async Task<DataSourceKnowledge> GetDataSourceKnowledgeAsync(int dataSourceId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var dataSource = await context.DataSources
            .Where(ds => ds.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var tables = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .ToListAsync(ct);

        var qualityScores = await context.DataQualityScores
            .Where(q => q.DataSourceId == dataSourceId)
            .ToListAsync(ct);

        var codeRefCount = await context.CodeReferences
            .CountAsync(r => r.GitHubRepository.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId), ct);

        var hasDoc = await context.DataSourceDocumentations
            .AnyAsync(d => d.DataSourceId == dataSourceId, ct);

        // DataQualityScore property is Score, not OverallScore
        var schemas = tables
            .GroupBy(t => t.SchemaName)
            .Select(g => new SchemaOverview(
                g.Key,
                g.Count(),
                qualityScores.Where(q => q.SchemaName == g.Key).Select(q => (double?)q.Score).Average()
            ))
            .ToList();

        return new DataSourceKnowledge
        {
            DataSourceId = dataSourceId,
            Name = dataSource.Name,
            DatabaseEngine = dataSource.DatabaseEngineType?.ToString(),
            TableCount = tables.Count,
            OverallQualityScore = qualityScores.Count != 0 ? qualityScores.Average(q => q.Score) : null,
            CodeReferenceCount = codeRefCount,
            HasDocumentation = hasDoc,
            Schemas = schemas
        };
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int? dataSourceId = null, int maxResults = 20, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var results = new List<SearchResult>();
        var queryLower = query.ToLower();

        // Search tables by name or description
        var tablesQuery = context.DatabaseMetadata.AsQueryable();
        if (dataSourceId.HasValue)
            tablesQuery = tablesQuery.Where(m => m.DataSourceId == dataSourceId.Value);

        var matchingTables = await tablesQuery
            .Where(m => m.TableName.ToLower().Contains(queryLower) ||
                        (m.TableDescription != null && m.TableDescription.ToLower().Contains(queryLower)))
            .Select(m => new { m.DataSourceId, DataSourceName = m.DataSource.Name, m.SchemaName, m.TableName, m.TableDescription })
            .Take(maxResults)
            .ToListAsync(ct);

        foreach (var table in matchingTables)
        {
            var relevance = table.TableName.Equals(query, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.8;
            results.Add(new SearchResult
            {
                Type = "table",
                DataSourceName = table.DataSourceName,
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                Description = table.TableDescription,
                Relevance = relevance
            });
        }

        // Search columns by name or description
        var columnsQuery = context.ColumnMetadata.AsQueryable();
        if (dataSourceId.HasValue)
            columnsQuery = columnsQuery.Where(c => c.DatabaseMetadata.DataSourceId == dataSourceId.Value);

        var matchingColumns = await columnsQuery
            .Where(c => c.ColumnName.ToLower().Contains(queryLower) ||
                        (c.Description != null && c.Description.ToLower().Contains(queryLower)))
            .Select(c => new
            {
                DataSourceName = c.DatabaseMetadata.DataSource.Name,
                c.DatabaseMetadata.SchemaName,
                c.DatabaseMetadata.TableName,
                c.ColumnName,
                c.Description
            })
            .Take(maxResults)
            .ToListAsync(ct);

        foreach (var col in matchingColumns)
        {
            results.Add(new SearchResult
            {
                Type = "column",
                DataSourceName = col.DataSourceName,
                SchemaName = col.SchemaName,
                TableName = col.TableName,
                ColumnName = col.ColumnName,
                Description = col.Description,
                Relevance = col.ColumnName.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.6
            });
        }

        // Search documentation sections - content is in AiGeneratedContent / UserEditedContent
        var docSections = await context.DocumentationSections
            .Where(d => d.AiGeneratedContent.ToLower().Contains(queryLower) ||
                        (d.UserEditedContent != null && d.UserEditedContent.ToLower().Contains(queryLower)))
            .Select(d => new
            {
                DataSourceName = d.Documentation.DataSource.Name,
                d.TableName,
                d.AiGeneratedContent,
                d.UserEditedContent,
                d.IsUserEdited
            })
            .Take(maxResults / 2)
            .ToListAsync(ct);

        foreach (var section in docSections)
        {
            var content = section.IsUserEdited ? section.UserEditedContent : section.AiGeneratedContent;
            results.Add(new SearchResult
            {
                Type = "documentation",
                DataSourceName = section.DataSourceName,
                SchemaName = string.Empty,
                TableName = section.TableName ?? string.Empty,
                Description = TruncateContent(content, 200),
                Relevance = 0.5
            });
        }

        return results.OrderByDescending(r => r.Relevance).Take(maxResults).ToList();
    }

    public async Task<LineageInfo> GetLineageAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Get code references that touch this table
        var codeRefs = await context.CodeReferences
            .Where(r => r.GitHubRepository.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId))
            .Where(r => r.TableName != null && r.TableName.ToLower() == tableName.ToLower())
            .ToListAsync(ct);

        var writtenBy = codeRefs
            .Where(r => r.ReferenceType is CodeReferenceType.DapperQuery or CodeReferenceType.RawSql or CodeReferenceType.Migration)
            .Select(r => new LineageNode("code", r.ClassName ?? r.FilePath, r.MethodName))
            .Distinct()
            .ToList();

        var readBy = codeRefs
            .Where(r => r.ReferenceType is CodeReferenceType.EntityModel or CodeReferenceType.ApiEndpoint or CodeReferenceType.DbContextConfiguration)
            .Select(r => new LineageNode("code", r.ClassName ?? r.FilePath, r.MethodName))
            .Distinct()
            .ToList();

        // Get FK relationships from metadata
        var metadata = await context.DatabaseMetadata
            .Include(m => m.Columns)
            .Where(m => m.DataSourceId == dataSourceId && m.SchemaName == schemaName && m.TableName == tableName)
            .FirstOrDefaultAsync(ct);

        var relatedTables = new List<LineageNode>();
        if (metadata != null)
        {
            foreach (var fk in metadata.Columns.Where(c => c.ForeignKeyTable != null))
                relatedTables.Add(new LineageNode("foreignKey", fk.ForeignKeyTable!, $"{fk.ColumnName} -> {fk.ForeignKeyColumn}"));
        }

        // Find tables that reference this table (reverse FK lookup)
        var referencingColumns = await context.ColumnMetadata
            .Where(c => c.DatabaseMetadata.DataSourceId == dataSourceId && c.ForeignKeyTable == tableName)
            .Select(c => new { c.DatabaseMetadata.SchemaName, c.DatabaseMetadata.TableName, c.ColumnName })
            .ToListAsync(ct);

        foreach (var col in referencingColumns)
            relatedTables.Add(new LineageNode("referencedBy", $"{col.SchemaName}.{col.TableName}", col.ColumnName));

        return new LineageInfo
        {
            SchemaName = schemaName,
            TableName = tableName,
            WrittenBy = writtenBy,
            ReadBy = readBy,
            RelatedTables = relatedTables
        };
    }

    public async Task<string> GetContextForLlmAsync(int dataSourceId, string? schemaName = null, string? tableName = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        if (tableName != null && schemaName != null)
        {
            var knowledge = await GetTableKnowledgeAsync(dataSourceId, schemaName, tableName, ct);
            sb.AppendLine($"# Table: {schemaName}.{tableName}");
            if (knowledge.Description != null) sb.AppendLine($"Description: {knowledge.Description}");
            if (knowledge.BusinessPurpose != null) sb.AppendLine($"Business Purpose: {knowledge.BusinessPurpose}");
            sb.AppendLine("\n## Columns:");
            foreach (var col in knowledge.Columns)
            {
                sb.Append($"  - {col.Name} ({col.DataType})");
                if (col.IsPrimaryKey) sb.Append(" [PK]");
                if (!col.IsNullable) sb.Append(" NOT NULL");
                if (col.ForeignKeyTable != null) sb.Append($" -> {col.ForeignKeyTable}.{col.ForeignKeyColumn}");
                if (col.Description != null) sb.Append($" -- {col.Description}");
                sb.AppendLine();
            }
            if (knowledge.QualityScore.HasValue)
                sb.AppendLine($"\nData Quality Score: {knowledge.QualityScore:F0}% ({knowledge.QualityTrend})");
        }
        else
        {
            var dsKnowledge = await GetDataSourceKnowledgeAsync(dataSourceId, ct);
            sb.AppendLine($"# Data Source: {dsKnowledge.Name} ({dsKnowledge.DatabaseEngine})");
            sb.AppendLine($"Tables: {dsKnowledge.TableCount}");
            if (dsKnowledge.OverallQualityScore.HasValue)
                sb.AppendLine($"Overall Quality: {dsKnowledge.OverallQualityScore:F0}%");
            sb.AppendLine("\n## Schemas:");
            foreach (var schema in dsKnowledge.Schemas)
                sb.AppendLine($"  - {schema.SchemaName}: {schema.TableCount} tables");

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var tablesQuery = context.DatabaseMetadata
                .Where(m => m.DataSourceId == dataSourceId);
            if (schemaName != null)
                tablesQuery = tablesQuery.Where(m => m.SchemaName == schemaName);

            var tables = await tablesQuery
                .OrderBy(m => m.SchemaName).ThenBy(m => m.TableName)
                .Select(m => new
                {
                    m.SchemaName,
                    m.TableName,
                    m.TableDescription,
                    Columns = m.Columns.Select(c => new
                    {
                        c.ColumnName,
                        c.DataType,
                        c.IsPrimaryKey,
                        c.ForeignKeyTable,
                        c.ForeignKeyColumn
                    }).ToList()
                })
                .ToListAsync(ct);

            sb.AppendLine("\n## Tables:");
            var charBudget = 8000;
            foreach (var t in tables)
            {
                sb.Append($"  - {t.SchemaName}.{t.TableName}");
                if (t.TableDescription != null) sb.Append($" -- {t.TableDescription}");

                // Include columns in compact format if within budget
                if (t.Columns.Count > 0 && sb.Length < charBudget)
                {
                    var colParts = t.Columns.Select(c =>
                    {
                        var part = $"{c.ColumnName} {c.DataType}";
                        if (c.IsPrimaryKey) part += " PK";
                        if (c.ForeignKeyTable != null) part += $" FK→{c.ForeignKeyTable}.{c.ForeignKeyColumn}";
                        return part;
                    });
                    sb.Append($" ({string.Join(", ", colParts)})");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string? TruncateContent(string? content, int maxLength)
    {
        if (content == null) return null;
        return content.Length <= maxLength ? content : content[..maxLength] + "...";
    }
}
