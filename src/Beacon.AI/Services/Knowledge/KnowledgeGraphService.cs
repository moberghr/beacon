using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Beacon.AI.Services.Knowledge;

internal sealed partial class KnowledgeGraphService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    ISharedSourceSettings sharedSourceSettings,
    IBeaconEmbeddingService embeddingService,
    ISchemaGraphService schemaGraphService,
    IValueGroundingService valueGroundingService,
    IConfiguredDatabaseResolver configuredDatabases,
    IQueryGuardrailService guardrail,
    ILogger<KnowledgeGraphService> logger,
    WithheldGroundingLog? withheldLog = null,
    IOptions<McpDeploymentOptions>? deploymentOptions = null,
    CatalogSnapshotCache? catalogSnapshots = null) : IKnowledgeGraphService
{
    // Grounding a prompt leaves out is logged once per row and interval (the singleton in the host; per instance in tests).
    private readonly WithheldGroundingLog _withheld = withheldLog ?? new WithheldGroundingLog();

    // Detailed-table budget for the smart-retrieval path. Kept as a named constant because the coverage
    // block reports against it — a silently different cap and reported cap would misdescribe the context.
    private const int MaxDetailedTables = 20;

    // Cap on join paths rendered into the small-schema fast path. A 40-table schema has 780 possible
    // pairs; past a few dozen paths the block stops grounding and starts crowding out the schema itself.
    private const int MaxRenderedJoinPaths = 40;

    // EF Core provider name for the Npgsql backend. Compared as a string so Beacon.AI stays off the
    // Npgsql package (§2.4) yet can branch to the pgvector <=> raw query on PostgreSQL data sources.
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Owner-type filters for the shared nearest-neighbour helpers. Metadata (table/column) feeds the
    // dense arm of hybrid search (B5); Exemplar feeds semantic few-shot selection (B6). Kept as int[]
    // so the PostgreSQL raw query can pass them as a single parameterized array (owner_type = ANY(...)).
    private static readonly int[] MetadataOwnerTypes =
        [(int)McpEmbeddingOwnerType.MetadataTable, (int)McpEmbeddingOwnerType.MetadataColumn];
    private static readonly int[] ExemplarOwnerTypes = [(int)McpEmbeddingOwnerType.Exemplar];

    // Project-scoped Tier-3 owner types for the same shared nearest-neighbour helpers. DocChunk feeds
    // the knowledge-answer top-K path (B3); GlossaryTerm feeds glossary injection (B4). Their embeddings
    // are project-scoped, so callers pass a projectId to the NN helpers instead of a dataSourceId.
    private static readonly int[] DocChunkOwnerTypes = [(int)McpEmbeddingOwnerType.DocChunk];
    private static readonly int[] GlossaryOwnerTypes = [(int)McpEmbeddingOwnerType.GlossaryTerm];

    // Golden eval cases are data-source-scoped (like Exemplar/metadata), so retrieval passes a dataSourceId
    // (not a projectId) to the shared nearest-neighbour helpers.
    private static readonly int[] GoldenCaseOwnerTypes = [(int)McpEmbeddingOwnerType.GoldenCase];

    public Task<TableKnowledge> GetTableKnowledgeAsync(int dataSourceId, int projectId, string schemaName, string tableName, CancellationToken ct = default)
    {
        return BuildTableKnowledgeAsync(dataSourceId, projectId, schemaName, tableName, ct);
    }

    // Only through the caller's project: a data source it does not link is not found. Documentation and code references
    // belong to a project: they come from projectId alone, never from another project that links the same data source.
    private async Task<TableKnowledge> BuildTableKnowledgeAsync(int dataSourceId, int projectId, string schemaName, string tableName, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var dataSource = await LinkedDataSourceAsync(context, dataSourceId, projectId, ct);

        // Get schema metadata
        var metadata = await context.DatabaseMetadata
            .Include(m => m.Columns)
            .Include(m => m.Indexes)
            .Where(m => m.DataSourceId == dataSourceId && m.SchemaName == schemaName && m.TableName == tableName)
            .FirstOrDefaultAsync(ct);

        // Get documentation from the project's documentation (DataModel section)
        var docContent = await context.ProjectDocumentationSections
            .Where(x => x.Documentation.ProjectId == projectId)
            .Where(x => x.Documentation.Project.DataSources.Any(y => y.DataSourceId == dataSourceId))
            .Where(x => x.SectionType == ProjectDocSectionType.DataModel)
            .OrderByDescending(x => x.Documentation.GeneratedAt)
            .Select(x => x.Content)
            .FirstOrDefaultAsync(ct);

        // Get code references (from the project's repositories, GitHub scanner)
        var codeReferences = await context.CodeReferences
            .Where(x => x.GitHubRepository.ProjectId == projectId)
            .Where(x => x.GitHubRepository.Project.DataSources.Any(y => y.DataSourceId == dataSourceId))
            .Where(x => x.TableName != null)
            .Where(x => x.TableName!.ToLower() == tableName.ToLower())
            .Take(50)
            .Select(x =>
                new CodeReferenceInfo(x.FilePath, x.LineNumber, x.ReferenceType, x.ClassName, x.MethodName, x.CodeSnippet))
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
            CodeReferences = codeReferences,
            // DataQualityScore property is Score, not OverallScore
            QualityScore = qualityScore?.Score,
            QualityTrend = qualityScore?.TrendDirection.ToString(),
            QualityRules = qualityRules,
            QueryCount = queryCount
        };
    }

    public Task<DataSourceKnowledge> GetDataSourceKnowledgeAsync(int dataSourceId, int projectId, CancellationToken ct = default)
    {
        return DataSourceKnowledgeAsync(dataSourceId, projectId, ct);
    }

    public async Task<LineageInfo> GetLineageAsync(int dataSourceId, int projectId, string schemaName, string tableName, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Only through the caller's project: a data source it does not link is not found.
        if (!await ProjectSourceIds(context, projectId).ContainsAsync(dataSourceId, ct))
        {
            throw new InvalidOperationException($"Data source {dataSourceId} not found");
        }

        // Get code references that touch this table, from the project's repositories only (never another project's
        // that links the same data source)
        var codeRefs = await context.CodeReferences
            .Where(x => x.GitHubRepository.ProjectId == projectId)
            .Where(x => x.GitHubRepository.Project.DataSources.Any(y => y.DataSourceId == dataSourceId))
            .Where(x => x.TableName != null)
            .Where(x => x.TableName!.ToLower() == tableName.ToLower())
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

    public async Task<string> GetContextForLlmAsync(int dataSourceId, int projectId, string? schemaName = null, string? tableName = null, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await LinkedDataSourceAsync(context, dataSourceId, projectId, ct);

        var isApi = dataSource.DataSourceType == DataSourceType.Api;
        var sb = new StringBuilder();

        if (tableName != null && schemaName != null)
        {
            // The table's columns and quality, with the caller's project's documentation only.
            var knowledge = await BuildTableKnowledgeAsync(dataSourceId, projectId, schemaName, tableName, ct);

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
            // The LLM context shows neither code references nor documentation, so neither is read.
            var dsKnowledge = await DataSourceKnowledgeAsync(dataSourceId, null, ct);

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
            var dsContext = await GetContextForLlmAsync(ds.DataSourceId, projectId, ct: ct);
            sb.AppendLine(dsContext);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<List<DataSourceKnowledge>> GetProjectDataSourcesAsync(int projectId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dsIds = await context.ProjectDataSources
            .WhereReadable()
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);
        if (dsIds.Count == 0)
        {
            return [];
        }

        // One query of each kind for all the sources the project links, not one round of them per source. A linked source
        // that is not found (archived) fails the call, as it did when each source was read on its own.
        var dataSources = await context.DataSources
            .Where(x => dsIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.Name,
                    x.DataSourceType,
                    x.DatabaseEngineType
                })
            .ToListAsync(ct);

        var schemaTableCounts = await context.DatabaseMetadata
            .Where(x => dsIds.Contains(x.DataSourceId))
            .GroupBy(x => new { x.DataSourceId, x.SchemaName })
            .Select(x =>
                new
                {
                    x.Key.DataSourceId,
                    x.Key.SchemaName,
                    TableCount = x.Count()
                })
            .ToListAsync(ct);

        var schemaQuality = await context.DataQualityScores
            .Where(x => dsIds.Contains(x.DataSourceId))
            .GroupBy(x => new { x.DataSourceId, x.SchemaName })
            .Select(x =>
                new
                {
                    x.Key.DataSourceId,
                    x.Key.SchemaName,
                    AverageScore = x.Average(y => (double?)y.Score),
                    ScoreCount = x.Count()
                })
            .ToListAsync(ct);

        // Code references and documentation belong to the project: the same for each of its sources.
        var codeRefCount = await context.CodeReferences
            .Where(x => x.GitHubRepository.ProjectId == projectId)
            .CountAsync(ct);

        var hasDoc = await context.ProjectDocumentations
            .Where(x => x.ProjectId == projectId)
            .AnyAsync(ct);

        var results = new List<DataSourceKnowledge>();
        foreach (var dsId in dsIds)
        {
            var dataSource = dataSources
                .Where(x => x.Id == dsId)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Data source {dsId} not found");
            var tableCounts = schemaTableCounts
                .Where(x => x.DataSourceId == dsId)
                .Select(x => (x.SchemaName, x.TableCount))
                .ToList();
            var quality = schemaQuality
                .Where(x => x.DataSourceId == dsId)
                .Select(x => (x.SchemaName, x.AverageScore, x.ScoreCount))
                .ToList();

            results.Add(DataSourceKnowledgeOf(dsId, dataSource.Name, dataSource.DataSourceType, dataSource.DatabaseEngineType, tableCounts, quality, codeRefCount, hasDoc));
        }

        return results;
    }

    /// <summary>
    /// Join paths for the whole schema, used on the small-schema fast path where every table is already
    /// described. Bounded by <see cref="MaxRenderedJoinPaths"/> so a densely-connected small schema
    /// cannot flood the prompt. Fails CLOSED — no paths rather than no answer.
    /// </summary>
    private async Task<IReadOnlyList<SchemaJoinPath>> GetJoinPathsForAllTablesAsync(
        int dataSourceId,
        CancellationToken ct)
    {
        try
        {
            var graph = await schemaGraphService.GetGraphAsync(dataSourceId, ct);
            if (graph.EdgeCount == 0)
            {
                return [];
            }

            var names = graph.Nodes
                .Select(x => x.QualifiedName)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var paths = new List<SchemaJoinPath>();
            for (var i = 0; i < names.Count && paths.Count < MaxRenderedJoinPaths; i++)
            {
                for (var j = i + 1; j < names.Count && paths.Count < MaxRenderedJoinPaths; j++)
                {
                    // Only direct and link-table-bridged joins here: the full pairwise set on a 40-table
                    // schema is 780 paths, and the long ones add noise rather than grounding.
                    var path = graph.FindPath(names[i], names[j], maxDepth: 2);
                    if (path != null)
                    {
                        paths.Add(path);
                    }
                }
            }

            return paths;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Schema graph join-path rendering failed for data source {DataSourceId}; context sent without join paths ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
            return [];
        }
    }

    /// <summary>
    /// Expands the retrieved tables into the set worth describing in full, using the registered
    /// relationship graph so join paths, link tables and multi-hop routes are grounded rather than
    /// guessed. Fails CLOSED: a graph-construction error degrades to the pre-graph one-hop foreign-key
    /// expansion, so a question that answered before this feature existed still answers.
    /// </summary>
    private async Task<SchemaExpansion> ExpandRelatedTablesAsync(
        int dataSourceId,
        IReadOnlyCollection<string> matchedTableNames,
        IReadOnlyList<(string SchemaName, string TableName, IReadOnlyList<SchemaColumn> Columns)> allTables,
        CancellationToken ct)
    {
        try
        {
            var graph = await schemaGraphService.GetGraphAsync(dataSourceId, ct);
            if (graph.EdgeCount > 0)
            {
                return graph.Expand(matchedTableNames, MaxDetailedTables);
            }

            // No registered relationships yet (metadata refreshed before this feature, or a warehouse
            // whose inference found nothing). The foreign-key columns still carry information.
            logger.LogDebug("Schema graph for data source {DataSourceId} has no edges; using foreign-key expansion.", dataSourceId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Schema graph expansion failed for data source {DataSourceId}; falling back to foreign-key expansion ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
        }

        return ExpandByForeignKeyHop(matchedTableNames, allTables);
    }

    /// <summary>
    /// Pre-graph fallback: one foreign-key hop in each direction. Target resolution goes through
    /// <see cref="ForeignKeyTargetResolver"/>, so a duplicate table name across schemas resolves
    /// correctly instead of picking whichever table sorts first.
    /// </summary>
    private static SchemaExpansion ExpandByForeignKeyHop(
        IReadOnlyCollection<string> matchedTableNames,
        IReadOnlyList<(string SchemaName, string TableName, IReadOnlyList<SchemaColumn> Columns)> allTables)
    {
        var resolverTables = allTables
            .Select(x =>
                new TableMetadataDto(
                    x.SchemaName,
                    x.TableName,
                    x.Columns
                        .Select(y =>
                            new ColumnMetadataDto(
                                y.ColumnName, y.DataType, y.IsNullable, y.IsPrimaryKey,
                                y.ForeignKeyTable != null, 0, y.ForeignKeyTable, y.ForeignKeyColumn,
                                null, null, null, null, y.ForeignKeySchema, null))
                        .ToList(),
                    [],
                    null))
            .ToList();

        var matched = new HashSet<string>(matchedTableNames, StringComparer.OrdinalIgnoreCase);
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in resolverTables)
        {
            var qualifiedName = $"{table.SchemaName}.{table.TableName}";
            var foreignKeyColumns = table.Columns
                .Where(x => x.ForeignKeyTable != null)
                .ToList();

            foreach (var column in foreignKeyColumns)
            {
                var target = ForeignKeyTargetResolver.Resolve(resolverTables, table.SchemaName, column);
                if (target == null)
                {
                    continue;
                }

                var targetName = $"{target.SchemaName}.{target.TableName}";

                // Forward: a matched table references this target.
                if (matched.Contains(qualifiedName))
                {
                    related.Add(targetName);
                }

                // Reverse: this table references a matched target.
                if (matched.Contains(targetName))
                {
                    related.Add(qualifiedName);
                }
            }
        }

        related.ExceptWith(matched);

        var detailed = new HashSet<string>(matched, StringComparer.OrdinalIgnoreCase);
        var remainingSlots = MaxDetailedTables - detailed.Count;
        var admitted = remainingSlots > 0 ? related.Take(remainingSlots).ToList() : [];
        detailed.UnionWith(admitted);

        var omitted = related.Count - admitted.Count;

        return new SchemaExpansion([.. detailed], [], Capped: omitted > 0, OmittedTableCount: omitted);
    }

    // The data sources projectId links. Grounding reads a data source only through the caller's authorized project
    // (§1.12): a source the project does not link is never searched and is reported as not found.
    private static IQueryable<int> ProjectSourceIds(BeaconContext context, int projectId)
    {
        return context.ProjectDataSources
            .WhereReadable()
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.DataSourceId);
    }

    private static async Task<DataSource> LinkedDataSourceAsync(
        BeaconContext context, int dataSourceId, int projectId, CancellationToken ct)
    {
        var projectSourceIds = ProjectSourceIds(context, projectId);

        return await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .Where(x => projectSourceIds.Contains(x.Id))
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");
    }

    // A data source's overview; code references and documentation of projectId only (none read without one). With a
    // project, only through it: a data source it does not link is not found. Without one, the caller has already checked
    // the link (the LLM context).
    private async Task<DataSourceKnowledge> DataSourceKnowledgeAsync(int dataSourceId, int? projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var dataSource = projectId == null
            ? await context.DataSources
                .Where(x => x.Id == dataSourceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {dataSourceId} not found")
            : await LinkedDataSourceAsync(context, dataSourceId, projectId.Value, ct);

        // Aggregate server-side — materializing every metadata/quality row here made each
        // `ask` pay for full-catalog loads per data source.
        var schemaTableCounts = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == dataSourceId)
            .GroupBy(x => x.SchemaName)
            .Select(x => new { SchemaName = x.Key, TableCount = x.Count() })
            .ToListAsync(ct);

        var schemaQuality = await context.DataQualityScores
            .Where(x => x.DataSourceId == dataSourceId)
            .GroupBy(x => x.SchemaName)
            .Select(x => new { SchemaName = x.Key, AverageScore = x.Average(y => (double?)y.Score), ScoreCount = x.Count() })
            .ToListAsync(ct);

        // Code references and documentation belong to a project: only the caller's project counts. Without one (the LLM
        // context, which shows neither) they are not read.
        var codeRefCount = projectId == null
            ? 0
            : await context.CodeReferences
                .Where(x => x.GitHubRepository.ProjectId == projectId)
                .CountAsync(ct);

        var hasDoc = projectId != null
            && await context.ProjectDocumentations
                .Where(x => x.ProjectId == projectId)
                .AnyAsync(ct);

        return DataSourceKnowledgeOf(
            dataSourceId,
            dataSource.Name,
            dataSource.DataSourceType,
            dataSource.DatabaseEngineType,
            schemaTableCounts.Select(x => (x.SchemaName, x.TableCount)).ToList(),
            schemaQuality.Select(x => (x.SchemaName, x.AverageScore, x.ScoreCount)).ToList(),
            codeRefCount,
            hasDoc);
    }

    // A data source's overview from its per-schema table counts and quality scores.
    private static DataSourceKnowledge DataSourceKnowledgeOf(
        int dataSourceId,
        string name,
        DataSourceType dataSourceType,
        DatabaseEngineType? databaseEngineType,
        List<(string SchemaName, int TableCount)> schemaTableCounts,
        List<(string SchemaName, double? AverageScore, int ScoreCount)> schemaQuality,
        int codeRefCount,
        bool hasDoc)
    {
        // DataQualityScore property is Score, not OverallScore
        var schemas = schemaTableCounts
            .Select(x => new SchemaOverview(
                x.SchemaName,
                x.TableCount,
                schemaQuality
                    .Where(y => y.SchemaName == x.SchemaName)
                    .Select(y => y.AverageScore)
                    .FirstOrDefault()
            ))
            .ToList();

        var totalScoreCount = schemaQuality.Sum(x => x.ScoreCount);

        return new DataSourceKnowledge
        {
            DataSourceId = dataSourceId,
            Name = name,
            DataSourceType = dataSourceType,
            DatabaseEngine = dataSourceType == DataSourceType.Api
                ? ConnectorRegistry.GetDisplayName(DataSourceType.Api)
                : databaseEngineType?.ToString(),
            TableCount = schemaTableCounts.Sum(x => x.TableCount),
            OverallQualityScore = totalScoreCount != 0
                ? schemaQuality.Sum(x => (x.AverageScore ?? 0) * x.ScoreCount) / totalScoreCount
                : null,
            CodeReferenceCount = codeRefCount,
            HasDocumentation = hasDoc,
            Schemas = schemas
        };
    }

    // The stored catalog of dataSourceId, from the snapshot cache when the host has one. The caller has checked that projectId
    // links the source and may read it; the snapshot is keyed by both (CatalogSnapshotCache).
    private Task<StoredCatalog> StoredCatalogAsync(BeaconContext context, int dataSourceId, int projectId, CancellationToken ct)
    {
        return catalogSnapshots == null
            ? LoadStoredCatalogAsync(context, dataSourceId, ct)
            : catalogSnapshots.GetOrLoadAsync(
                projectId,
                dataSourceId,
                x => LoadStoredCatalogAsync(context, dataSourceId, x),
                ApproximateBytes,
                ct);
    }

    // What a stored catalog takes in memory, roughly: its strings (two bytes a character) and a fixed overhead per table
    // and column. Stored sample values dominate on a sampled source.
    internal static long ApproximateBytes(StoredCatalog catalog)
    {
        const int TableOverhead = 96;
        const int ColumnOverhead = 160;
        long bytes = 0;
        foreach (var table in catalog.Tables)
        {
            bytes += TableOverhead + Chars(table.SchemaName) + Chars(table.TableName) + Chars(table.TableDescription);
            foreach (var column in table.Columns)
            {
                bytes += ColumnOverhead
                    + Chars(column.ColumnName)
                    + Chars(column.DataType)
                    + Chars(column.Description)
                    + Chars(column.SampleValuesJson)
                    + Chars(column.ForeignKeySchema)
                    + Chars(column.ForeignKeyTable)
                    + Chars(column.ForeignKeyColumn);
            }
        }

        return bytes;

        static long Chars(string? value) => 2L * (value?.Length ?? 0);
    }

    // All tables and columns of the data source in one query, as stored (samples not screened).
    private static async Task<StoredCatalog> LoadStoredCatalogAsync(BeaconContext context, int dataSourceId, CancellationToken ct)
    {
        var tables = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == dataSourceId)
            .OrderBy(x => x.SchemaName)
            .ThenBy(x => x.TableName)
            .Select(x =>
                new CatalogTable(
                    x.SchemaName,
                    x.TableName,
                    x.TableDescription,
                    x.Columns
                        .Select(y =>
                            new SchemaColumn(
                                y.ColumnName, y.DataType, y.IsPrimaryKey, y.IsNullable,
                                y.ForeignKeyTable, y.ForeignKeyColumn, y.Description,
                                y.MaxLength, y.SampleValues, y.ForeignKeySchema, y.SampleValuesComplete))
                        .ToList()))
            .ToListAsync(ct);

        return new StoredCatalog(tables);
    }
}

// A data source's stored catalog as the ask path reads it: tables in schema and name order, stored sample values not yet
// screened. Cached as it is (CatalogSnapshotCache), so never changed in place.
internal sealed record StoredCatalog(IReadOnlyList<CatalogTable> Tables);

internal sealed record CatalogTable(string SchemaName, string TableName, string? TableDescription, IReadOnlyList<SchemaColumn> Columns);
