using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using System.Text;

namespace Beacon.AI.Services.Knowledge;

internal sealed class KnowledgeGraphService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
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

        // Get documentation from project documentation (DataModel section)
        var docContent = await context.ProjectDocumentationSections
            .Where(s => s.Documentation.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId)
                && s.SectionType == ProjectDocSectionType.DataModel)
            .OrderByDescending(s => s.Documentation.GeneratedAt)
            .Select(s => s.Content)
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
            Description = docContent != null ? ExtractTableDoc(docContent, tableName) : null,
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

        var hasDoc = await context.ProjectDocumentations
            .AnyAsync(d => d.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId), ct);

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
            DataSourceType = dataSource.DataSourceType,
            DatabaseEngine = dataSource.DataSourceType == DataSourceType.Api
                ? ConnectorRegistry.GetDisplayName(DataSourceType.Api)
                : dataSource.DatabaseEngineType?.ToString(),
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
                DataSourceId = table.DataSourceId,
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
                c.DatabaseMetadata.DataSourceId,
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
                DataSourceId = col.DataSourceId,
                DataSourceName = col.DataSourceName,
                SchemaName = col.SchemaName,
                TableName = col.TableName,
                ColumnName = col.ColumnName,
                Description = col.Description,
                Relevance = col.ColumnName.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.6
            });
        }

        // Search project documentation sections
        var docSections = await context.ProjectDocumentationSections
            .Where(s => s.Content.ToLower().Contains(queryLower))
            .Select(s => new
            {
                ProjectName = s.Documentation.Project.Name,
                s.Title,
                s.Content
            })
            .Take(maxResults / 2)
            .ToListAsync(ct);

        foreach (var docSection in docSections)
        {
            results.Add(new SearchResult
            {
                Type = "documentation",
                DataSourceName = docSection.ProjectName,
                SchemaName = string.Empty,
                TableName = string.Empty,
                Description = TruncateContent(docSection.Content, 200),
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
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var isApi = dataSource.DataSourceType == DataSourceType.Api;
        var sb = new StringBuilder();

        if (tableName != null && schemaName != null)
        {
            var knowledge = await GetTableKnowledgeAsync(dataSourceId, schemaName, tableName, ct);

            if (isApi)
            {
                sb.AppendLine($"# Endpoint: {tableName}");
                if (knowledge.Description != null) sb.AppendLine($"Description: {knowledge.Description}");
                if (knowledge.BusinessPurpose != null) sb.AppendLine($"Summary: {knowledge.BusinessPurpose}");
                sb.AppendLine("\n## Response Fields:");
                foreach (var col in knowledge.Columns)
                {
                    sb.Append($"  - {col.Name} ({col.DataType})");
                    if (col.Description != null) sb.Append($" -- {col.Description}");
                    sb.AppendLine();
                }
            }
            else
            {
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
            }

            if (knowledge.QualityScore.HasValue)
                sb.AppendLine($"\nData Quality Score: {knowledge.QualityScore:F0}% ({knowledge.QualityTrend})");
        }
        else
        {
            var dsKnowledge = await GetDataSourceKnowledgeAsync(dataSourceId, ct);

            if (isApi)
            {
                sb.AppendLine($"# REST API: {dsKnowledge.Name}");
                sb.AppendLine($"Endpoints: {dsKnowledge.TableCount}");
            }
            else
            {
                sb.AppendLine($"# Data Source: {dsKnowledge.Name} ({dsKnowledge.DatabaseEngine})");
                sb.AppendLine($"Tables: {dsKnowledge.TableCount}");
            }

            if (dsKnowledge.OverallQualityScore.HasValue)
                sb.AppendLine($"Overall Quality: {dsKnowledge.OverallQualityScore:F0}%");

            var groupLabel = isApi ? "Tags" : "Schemas";
            sb.AppendLine($"\n## {groupLabel}:");
            foreach (var schema in dsKnowledge.Schemas)
            {
                var itemLabel = isApi ? "endpoints" : "tables";
                sb.AppendLine($"  - {schema.SchemaName}: {schema.TableCount} {itemLabel}");
            }

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
                        c.ForeignKeyColumn,
                        c.Description
                    }).ToList()
                })
                .ToListAsync(ct);

            sb.AppendLine(isApi ? "\n## Endpoints:" : "\n## Tables:");
            var charBudget = 8000;
            foreach (var t in tables)
            {
                if (isApi)
                {
                    sb.Append($"  - {t.TableName}");
                    if (t.TableDescription != null) sb.Append($" -- {t.TableDescription}");
                    if (t.Columns.Count > 0 && sb.Length < charBudget)
                    {
                        var fieldParts = t.Columns.Select(c =>
                        {
                            var part = $"{c.ColumnName} {c.DataType}";
                            if (c.Description != null) part += $": {c.Description}";
                            return part;
                        });
                        sb.Append($" → returns ({string.Join(", ", fieldParts)})");
                    }
                }
                else
                {
                    sb.Append($"  - {t.SchemaName}.{t.TableName}");
                    if (t.TableDescription != null) sb.Append($" -- {t.TableDescription}");
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
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public async Task<List<SearchResult>> SearchProjectAsync(string query, int projectId, int maxResults = 20, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Get all data source IDs for this project
        var dsIds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        if (dsIds.Count == 0) return [];

        var results = new List<SearchResult>();
        var queryLower = query.ToLower();

        // Search tables
        var matchingTables = await context.DatabaseMetadata
            .Where(m => dsIds.Contains(m.DataSourceId))
            .Where(m => m.TableName.ToLower().Contains(queryLower) ||
                        (m.TableDescription != null && m.TableDescription.ToLower().Contains(queryLower)))
            .Select(m => new { m.DataSourceId, DataSourceName = m.DataSource.Name, m.SchemaName, m.TableName, m.TableDescription })
            .Take(maxResults)
            .ToListAsync(ct);

        foreach (var table in matchingTables)
        {
            results.Add(new SearchResult
            {
                Type = "table",
                DataSourceId = table.DataSourceId,
                DataSourceName = table.DataSourceName,
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                Description = table.TableDescription,
                Relevance = table.TableName.Equals(query, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.8
            });
        }

        // Search columns
        var matchingColumns = await context.ColumnMetadata
            .Where(c => dsIds.Contains(c.DatabaseMetadata.DataSourceId))
            .Where(c => c.ColumnName.ToLower().Contains(queryLower) ||
                        (c.Description != null && c.Description.ToLower().Contains(queryLower)))
            .Select(c => new
            {
                c.DatabaseMetadata.DataSourceId,
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
                DataSourceId = col.DataSourceId,
                DataSourceName = col.DataSourceName,
                SchemaName = col.SchemaName,
                TableName = col.TableName,
                ColumnName = col.ColumnName,
                Description = col.Description,
                Relevance = col.ColumnName.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.6
            });
        }

        // Search project documentation
        var docSections = await context.ProjectDocumentationSections
            .Where(s => s.Documentation.ProjectId == projectId)
            .Where(s => s.Content.ToLower().Contains(queryLower))
            .Select(s => new { ProjectName = s.Documentation.Project.Name, s.Title, s.Content })
            .Take(maxResults / 2)
            .ToListAsync(ct);

        foreach (var doc in docSections)
        {
            results.Add(new SearchResult
            {
                Type = "documentation",
                DataSourceName = doc.ProjectName,
                SchemaName = string.Empty,
                TableName = string.Empty,
                Description = TruncateContent(doc.Content, 200),
                Relevance = 0.5
            });
        }

        return results.OrderByDescending(r => r.Relevance).Take(maxResults).ToList();
    }

    public async Task<string> GetProjectContextForLlmAsync(int projectId, CancellationToken ct = default)
    {
        var dataSources = await GetProjectDataSourcesAsync(projectId, ct);
        var sb = new StringBuilder();

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var project = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Name, p.Description })
            .FirstOrDefaultAsync(ct);

        if (project == null)
            throw new InvalidOperationException($"Project {projectId} not found");

        sb.AppendLine($"# Project: {project.Name}");
        if (!string.IsNullOrEmpty(project.Description))
            sb.AppendLine(project.Description);
        sb.AppendLine($"\nData Sources: {dataSources.Count}");
        sb.AppendLine();

        foreach (var ds in dataSources)
        {
            sb.AppendLine($"## Data Source: {ds.Name} (ID: {ds.DataSourceId})");
            sb.AppendLine($"Type: {ds.DatabaseEngine ?? ds.DataSourceType.ToString()}, Tables: {ds.TableCount}");
            if (ds.OverallQualityScore.HasValue)
                sb.AppendLine($"Quality: {ds.OverallQualityScore:F0}%");

            // Get the full LLM context for this data source
            var dsContext = await GetContextForLlmAsync(ds.DataSourceId, ct: ct);
            sb.AppendLine(dsContext);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<List<DataSourceKnowledge>> GetProjectDataSourcesAsync(int projectId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dsIds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        var results = new List<DataSourceKnowledge>();
        foreach (var dsId in dsIds)
            results.Add(await GetDataSourceKnowledgeAsync(dsId, ct));

        return results;
    }

    public async Task<SmartSchemaContext> GetSmartContextForAskAsync(int dataSourceId, string question, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var isApi = dataSource.DataSourceType == DataSourceType.Api;

        // Load all tables + columns in one query
        var allTables = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .OrderBy(m => m.SchemaName).ThenBy(m => m.TableName)
            .Select(m => new
            {
                m.SchemaName,
                m.TableName,
                m.TableDescription,
                Columns = m.Columns.Select(c => new SchemaColumn(
                    c.ColumnName, c.DataType, c.IsPrimaryKey, c.IsNullable,
                    c.ForeignKeyTable, c.ForeignKeyColumn, c.Description
                )).ToList()
            })
            .ToListAsync(ct);

        var totalColumns = allTables.Sum(t => t.Columns.Count);
        var catalog = BuildSchemaCatalog(allTables.Select(t => (t.SchemaName, t.TableName, (IEnumerable<SchemaColumn>)t.Columns)));
        var dialect = dataSource.DatabaseEngineType?.ToString();
        var mcpSettings = await settingsProvider.GetSettingsAsync(ct);

        // Small schema fast path: send everything
        if (allTables.Count <= 40 || totalColumns <= 300)
        {
            var sb = new StringBuilder();
            sb.AppendLine(isApi
                ? $"# REST API: {dataSource.Name}"
                : $"# Data Source: {dataSource.Name} ({dataSource.DatabaseEngineType})");
            sb.AppendLine($"Tables: {allTables.Count}\n");

            foreach (var t in allTables)
                AppendTableWithFullColumns(sb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);

            // Inject learned patterns
            if (mcpSettings.EnableLearning)
            {
                var allTableNames = allTables.Select(t => $"{t.SchemaName}.{t.TableName}").ToList();
                var learnedPatterns = await GetRelevantPatternsAsync(dataSourceId, allTableNames, question,
                    budgetChars: mcpSettings.LearningInjectionBudgetChars, ct: ct);
                sb.Append(FormatLearnedPatternsForLlm(learnedPatterns));
            }

            return new SmartSchemaContext
            {
                FullContext = sb.ToString(),
                UsedSmartRetrieval = false,
                TotalTableCount = allTables.Count,
                DatabaseDialect = dialect,
                SchemaCatalog = catalog
            };
        }

        // Smart path: search for relevant tables
        var searchResults = await SearchAsync(question, dataSourceId, maxResults: 15, ct);

        var matchedTableNames = searchResults
            .Where(r => r.Type is "table" or "column")
            .Select(r => $"{r.SchemaName}.{r.TableName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Expand one FK hop: tables that matched tables reference + tables that reference matched tables
        var fkConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables.Where(t => matchedTableNames.Contains($"{t.SchemaName}.{t.TableName}")))
        {
            foreach (var col in t.Columns.Where(c => c.ForeignKeyTable != null))
            {
                // Find the schema for the FK target
                var fkTarget = allTables.FirstOrDefault(at =>
                    at.TableName.Equals(col.ForeignKeyTable, StringComparison.OrdinalIgnoreCase));
                if (fkTarget != null)
                    fkConnected.Add($"{fkTarget.SchemaName}.{fkTarget.TableName}");
            }
        }

        // Reverse FK: tables that reference matched tables
        foreach (var t in allTables)
        {
            var qualifiedName = $"{t.SchemaName}.{t.TableName}";
            if (matchedTableNames.Contains(qualifiedName)) continue;

            foreach (var col in t.Columns.Where(c => c.ForeignKeyTable != null))
            {
                if (allTables.Any(at =>
                    matchedTableNames.Contains($"{at.SchemaName}.{at.TableName}") &&
                    at.TableName.Equals(col.ForeignKeyTable, StringComparison.OrdinalIgnoreCase)))
                {
                    fkConnected.Add(qualifiedName);
                    break;
                }
            }
        }

        // Remove already-matched from FK set
        fkConnected.ExceptWith(matchedTableNames);

        // Cap detailed tables at ~20 (prioritize search matches over FK connections)
        var detailedTables = new HashSet<string>(matchedTableNames, StringComparer.OrdinalIgnoreCase);
        var remainingSlots = 20 - detailedTables.Count;
        if (remainingSlots > 0)
        {
            foreach (var fk in fkConnected.Take(remainingSlots))
                detailedTables.Add(fk);
        }

        // Build two-section context
        var smartSb = new StringBuilder();
        smartSb.AppendLine(isApi
            ? $"# REST API: {dataSource.Name}"
            : $"# Data Source: {dataSource.Name} ({dataSource.DatabaseEngineType})");
        smartSb.AppendLine($"Total tables: {allTables.Count}\n");

        // Section 1: Relevant tables with full schema
        smartSb.AppendLine("## Relevant Tables (full schema)\n");
        foreach (var t in allTables.Where(t => detailedTables.Contains($"{t.SchemaName}.{t.TableName}")))
            AppendTableWithFullColumns(smartSb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);

        // Section 2: Other tables compact
        var otherTables = allTables.Where(t => !detailedTables.Contains($"{t.SchemaName}.{t.TableName}")).ToList();
        if (otherTables.Count > 0)
        {
            smartSb.AppendLine("\n## Other Tables (name and primary keys only)\n");
            foreach (var t in otherTables)
                AppendTableCompact(smartSb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);
        }

        // Inject learned patterns for relevant tables
        if (mcpSettings.EnableLearning)
        {
            var relevantTableNames = detailedTables.ToList();
            var smartPatterns = await GetRelevantPatternsAsync(dataSourceId, relevantTableNames, question,
                budgetChars: mcpSettings.LearningInjectionBudgetChars, ct: ct);
            smartSb.Append(FormatLearnedPatternsForLlm(smartPatterns));
        }

        return new SmartSchemaContext
        {
            FullContext = smartSb.ToString(),
            UsedSmartRetrieval = true,
            RelevantTables = [.. detailedTables],
            TotalTableCount = allTables.Count,
            DatabaseDialect = dialect,
            SchemaCatalog = catalog
        };
    }

    public async Task<string> GetTablesContextAsync(int dataSourceId, IEnumerable<string> tableNames, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var isApi = dataSource.DataSourceType == DataSourceType.Api;
        var nameSet = tableNames.Select(n => n.ToLower()).ToHashSet();

        var tables = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .Select(m => new
            {
                m.SchemaName,
                m.TableName,
                m.TableDescription,
                Columns = m.Columns.Select(c => new SchemaColumn(
                    c.ColumnName, c.DataType, c.IsPrimaryKey, c.IsNullable,
                    c.ForeignKeyTable, c.ForeignKeyColumn, c.Description
                )).ToList()
            })
            .ToListAsync(ct);

        // Match by table name or schema.table
        var matched = tables.Where(t =>
            nameSet.Contains(t.TableName.ToLower()) ||
            nameSet.Contains($"{t.SchemaName}.{t.TableName}".ToLower()))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Table Schemas\n");
        foreach (var t in matched)
            AppendTableWithFullColumns(sb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);

        return sb.ToString();
    }

    private static void AppendTableWithFullColumns(
        StringBuilder sb, string schemaName, string tableName, string? description,
        IEnumerable<SchemaColumn> columns, bool isApi)
    {
        if (isApi)
        {
            sb.AppendLine($"### {tableName}");
            if (description != null) sb.AppendLine($"  {description}");
        }
        else
        {
            sb.AppendLine($"### {schemaName}.{tableName}");
            if (description != null) sb.AppendLine($"  {description}");
        }

        sb.AppendLine("  Columns:");
        foreach (var col in columns)
        {
            sb.Append($"    - {col.ColumnName} ({col.DataType})");
            if (col.IsPrimaryKey) sb.Append(" [PK]");
            if (!col.IsNullable) sb.Append(" NOT NULL");
            if (col.ForeignKeyTable != null) sb.Append($" FK→{col.ForeignKeyTable}.{col.ForeignKeyColumn}");
            if (col.Description != null) sb.Append($" -- {col.Description}");
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    private static void AppendTableCompact(
        StringBuilder sb, string schemaName, string tableName, string? description,
        IEnumerable<SchemaColumn> columns, bool isApi)
    {
        var columnList = columns.ToList();
        var pks = columnList.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName).ToList();
        var pkStr = pks.Count > 0 ? $"PK: {string.Join(", ", pks)}" : "no PK";
        var label = isApi ? tableName : $"{schemaName}.{tableName}";
        sb.Append($"  - {label} ({pkStr})");
        if (description != null) sb.Append($" -- {description}");
        sb.AppendLine();

        // Show all column names so LLM never has to guess
        var colNames = columnList.Select(c => c.ColumnName).ToList();
        if (colNames.Count > 0)
        {
            sb.AppendLine($"    Columns: {string.Join(", ", colNames)}");
        }
    }

    private record SchemaColumn(
        string ColumnName, string DataType, bool IsPrimaryKey, bool IsNullable,
        string? ForeignKeyTable, string? ForeignKeyColumn, string? Description);

    private static string? TruncateContent(string? content, int maxLength)
    {
        if (content == null) return null;
        return content.Length <= maxLength ? content : content[..maxLength] + "...";
    }

    private static Dictionary<string, HashSet<string>> BuildSchemaCatalog(
        IEnumerable<(string SchemaName, string TableName, IEnumerable<SchemaColumn> Columns)> tables)
    {
        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (schema, table, columns) in tables)
        {
            var cols = columns.Select(c => c.ColumnName.ToLowerInvariant()).ToHashSet();
            catalog[table.ToLowerInvariant()] = cols;
            catalog[$"{schema}.{table}".ToLowerInvariant()] = cols;
        }
        return catalog;
    }

    private static string? ExtractTableDoc(string dataModelContent, string tableName)
    {
        // Try to find the section about this specific table in the DataModel content
        var marker = tableName;
        var idx = dataModelContent.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // Extract a chunk of text around the table name (up to 500 chars after)
        var start = idx;
        var end = Math.Min(dataModelContent.Length, idx + 500);
        // Try to end at a paragraph boundary
        var nextBlank = dataModelContent.IndexOf("\n\n", start + marker.Length, StringComparison.Ordinal);
        if (nextBlank > 0 && nextBlank < end) end = nextBlank;

        return TruncateContent(dataModelContent[start..end], 400);
    }

    public async Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(
        int dataSourceId, List<string> tableNames, string? question = null,
        int maxPatterns = 10, int budgetChars = 1500, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var lowerTables = tableNames.Select(t => t.ToLowerInvariant()).ToHashSet();

        var patterns = await context.McpLearnedPatterns
            .Where(p => p.DataSourceId == dataSourceId
                && (p.Status == McpPatternStatus.Approved || p.Status == McpPatternStatus.AutoApproved))
            .OrderByDescending(p => p.Confidence)
            .ThenByDescending(p => p.SignalCount)
            .Take(100)
            .Select(p => new
            {
                p.PatternType,
                p.TableName,
                p.SchemaName,
                p.ColumnName,
                p.PatternContent,
                p.ExampleQuestion,
                p.ExampleSql,
                p.Confidence
            })
            .ToListAsync(ct);

        // Rank by relevance: table overlap + pattern type priority
        var ranked = patterns
            .Select(p =>
            {
                var tableFull = $"{p.SchemaName}.{p.TableName}".ToLowerInvariant();
                var tableOnly = p.TableName.ToLowerInvariant();
                var tableMatch = lowerTables.Contains(tableFull) || lowerTables.Contains(tableOnly);
                var typePriority = p.PatternType switch
                {
                    McpPatternType.SchemaCorrection => 4,
                    McpPatternType.ColumnClarification => 3,
                    McpPatternType.JoinPattern => 2,
                    McpPatternType.CommonQuery => 1,
                    _ => 0
                };
                var score = (tableMatch ? 10 : 0) + typePriority + p.Confidence;
                return (Pattern: p, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Apply budget cap
        var result = new List<LearnedPatternInfo>();
        var totalChars = 0;

        foreach (var (p, _) in ranked)
        {
            if (result.Count >= maxPatterns) break;
            var contentLen = p.PatternContent.Length + (p.ExampleSql?.Length ?? 0) + 20;
            if (totalChars + contentLen > budgetChars && result.Count > 0) break;

            totalChars += contentLen;
            result.Add(new LearnedPatternInfo(
                p.PatternType.ToString(), p.TableName, p.ColumnName,
                p.PatternContent, p.ExampleQuestion, p.ExampleSql, p.Confidence));
        }

        return result;
    }

    internal static string FormatLearnedPatternsForLlm(List<LearnedPatternInfo> patterns)
    {
        if (patterns.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("\n## Learned Patterns (from usage)\n");

        var corrections = patterns.Where(p => p.PatternType == "SchemaCorrection").ToList();
        if (corrections.Count > 0)
        {
            sb.AppendLine("### Known Corrections");
            foreach (var p in corrections)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        var clarifications = patterns.Where(p => p.PatternType == "ColumnClarification").ToList();
        if (clarifications.Count > 0)
        {
            sb.AppendLine("### Column Clarifications");
            foreach (var p in clarifications)
            {
                var colPart = p.ColumnName != null ? $".{p.ColumnName}" : "";
                sb.AppendLine($"- {p.TableName}{colPart}: {p.PatternContent}");
            }
            sb.AppendLine();
        }

        var joins = patterns.Where(p => p.PatternType == "JoinPattern").ToList();
        if (joins.Count > 0)
        {
            sb.AppendLine("### Join Patterns");
            foreach (var p in joins)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        var commonQueries = patterns.Where(p => p.PatternType == "CommonQuery").ToList();
        if (commonQueries.Count > 0)
        {
            sb.AppendLine("### Common Queries");
            foreach (var p in commonQueries)
            {
                if (!string.IsNullOrEmpty(p.ExampleQuestion))
                    sb.AppendLine($"- \"{p.ExampleQuestion}\"");
                else
                    sb.AppendLine($"- {p.PatternContent}");
                if (!string.IsNullOrEmpty(p.ExampleSql))
                    sb.AppendLine($"  → `{p.ExampleSql}`");
            }
            sb.AppendLine();
        }

        var mappings = patterns.Where(p => p.PatternType == "BusinessTermMapping").ToList();
        if (mappings.Count > 0)
        {
            sb.AppendLine("### Business Term Mappings");
            foreach (var p in mappings)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        var gaps = patterns.Where(p => p.PatternType == "DocumentationGap").ToList();
        if (gaps.Count > 0)
        {
            sb.AppendLine("### Documentation Gaps (caution)");
            foreach (var p in gaps)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
