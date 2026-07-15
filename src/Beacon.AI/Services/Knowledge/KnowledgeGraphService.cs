using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Embeddings;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using System.Globalization;
using System.Text;

namespace Beacon.AI.Services.Knowledge;

internal sealed class KnowledgeGraphService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IBeaconEmbeddingService embeddingService,
    ILogger<KnowledgeGraphService> logger) : IKnowledgeGraphService
{
    // EF Core provider name for the Npgsql backend. Compared as a string so Beacon.AI stays off the
    // Npgsql package (§2.4) yet can branch to the pgvector <=> raw query on PostgreSQL data sources.
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Owner-type filters for the shared nearest-neighbour helpers. Metadata (table/column) feeds the
    // dense arm of hybrid search (B5); Exemplar feeds semantic few-shot selection (B6). Kept as int[]
    // so the PostgreSQL raw query can pass them as a single parameterized array (owner_type = ANY(...)).
    private static readonly int[] MetadataOwnerTypes =
        [(int)McpEmbeddingOwnerType.MetadataTable, (int)McpEmbeddingOwnerType.MetadataColumn];
    private static readonly int[] ExemplarOwnerTypes = [(int)McpEmbeddingOwnerType.Exemplar];
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

        var codeRefCount = await context.CodeReferences
            .CountAsync(r => r.GitHubRepository.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId), ct);

        var hasDoc = await context.ProjectDocumentations
            .AnyAsync(d => d.Project.DataSources.Any(ds => ds.DataSourceId == dataSourceId), ct);

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
            Name = dataSource.Name,
            DataSourceType = dataSource.DataSourceType,
            DatabaseEngine = dataSource.DataSourceType == DataSourceType.Api
                ? ConnectorRegistry.GetDisplayName(DataSourceType.Api)
                : dataSource.DatabaseEngineType?.ToString(),
            TableCount = schemaTableCounts.Sum(x => x.TableCount),
            OverallQualityScore = totalScoreCount != 0
                ? schemaQuality.Sum(x => (x.AverageScore ?? 0) * x.ScoreCount) / totalScoreCount
                : null,
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

        // Tokenize the question so each meaningful term can match independently. Searching
        // for the whole question as a single substring never matches on real schemas.
        var terms = TokenizeQuery(query);

        // Fall back to the whole (lowercased) query when tokenization yields nothing useful.
        if (terms.Count == 0)
            terms = [queryLower];

        // Search tables by name or description — match ANY term.
        var tablesQuery = context.DatabaseMetadata.AsQueryable();
        if (dataSourceId.HasValue)
            tablesQuery = tablesQuery.Where(m => m.DataSourceId == dataSourceId.Value);

        var matchingTables = await tablesQuery
            .Where(m => terms.Any(t => m.TableName.ToLower().Contains(t)) ||
                        (m.TableDescription != null && terms.Any(t => m.TableDescription.ToLower().Contains(t))))
            .Select(m => new { m.DataSourceId, DataSourceName = m.DataSource.Name, m.SchemaName, m.TableName, m.TableDescription })
            .Take(maxResults * 2)
            .ToListAsync(ct);

        foreach (var table in matchingTables)
        {
            var overlap = CountOverlap(terms, table.TableName, table.TableDescription);
            var relevance = table.TableName.Equals(query, StringComparison.OrdinalIgnoreCase)
                ? 1.0
                : 0.8 + (overlap * 0.05);
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

        // Search columns by name or description — match ANY term.
        var columnsQuery = context.ColumnMetadata.AsQueryable();
        if (dataSourceId.HasValue)
            columnsQuery = columnsQuery.Where(c => c.DatabaseMetadata.DataSourceId == dataSourceId.Value);

        var matchingColumns = await columnsQuery
            .Where(c => terms.Any(t => c.ColumnName.ToLower().Contains(t)) ||
                        (c.Description != null && terms.Any(t => c.Description.ToLower().Contains(t))))
            .Select(c => new
            {
                c.DatabaseMetadata.DataSourceId,
                DataSourceName = c.DatabaseMetadata.DataSource.Name,
                c.DatabaseMetadata.SchemaName,
                c.DatabaseMetadata.TableName,
                c.ColumnName,
                c.Description
            })
            .Take(maxResults * 2)
            .ToListAsync(ct);

        foreach (var col in matchingColumns)
        {
            var overlap = CountOverlap(terms, col.ColumnName, col.Description);
            results.Add(new SearchResult
            {
                Type = "column",
                DataSourceId = col.DataSourceId,
                DataSourceName = col.DataSourceName,
                SchemaName = col.SchemaName,
                TableName = col.TableName,
                ColumnName = col.ColumnName,
                Description = col.Description,
                Relevance = col.ColumnName.Equals(query, StringComparison.OrdinalIgnoreCase)
                    ? 0.9
                    : 0.6 + (overlap * 0.05)
            });
        }

        // Search project documentation sections — match ANY term.
        var docSections = await context.ProjectDocumentationSections
            .Where(s => terms.Any(t => s.Content.ToLower().Contains(t)))
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

        // Lexical (sparse) arm — the historical token-overlap ranking. This is exactly the result
        // returned before hybrid retrieval existed, and stays the behaviour-preserving fallback.
        var lexicalRanked = results
            .OrderByDescending(r => r.Relevance)
            .ToList();

        // Dense arm only kicks in with a local embedder, semantic retrieval enabled, and a concrete
        // data source to scope the vector store to. Anything short of that returns today's lexical list.
        var useDense = embeddingService.IsAvailable && dataSourceId.HasValue;
        if (useDense)
        {
            var settings = await settingsProvider.GetSettingsAsync(ct);
            useDense = settings.EnableSemanticRetrieval;
        }

        if (!useDense)
        {
            return lexicalRanked.Take(maxResults).ToList();
        }

        try
        {
            var denseRanked = await BuildDenseResultsAsync(context, query, dataSourceId!.Value, maxResults * 2, ct);
            if (denseRanked.Count == 0)
            {
                // No vectors indexed yet (or nothing matched) — nothing to fuse; keep lexical behaviour.
                return lexicalRanked.Take(maxResults).ToList();
            }

            // Fuse the two independently-ranked arms via RRF and re-weight Relevance with the fused score.
            var fused = ReciprocalRankFusion.FuseWithScores(
                new IReadOnlyList<SearchResult>[] { lexicalRanked, denseRanked },
                FusionKey);

            return fused
                .Take(maxResults)
                .Select(x => x.Item with { Relevance = x.Score })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            // A cancelled/timed-out request must unwind, not be misreported as a dense-arm failure and
            // then keep doing more cancellable lexical work.
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed onto the lexical arm — a vector-store/query hiccup must never fail a search.
            logger.LogWarning(ex, "Dense retrieval arm failed for data source {DataSourceId}; falling back to lexical search.", dataSourceId);
            return lexicalRanked.Take(maxResults).ToList();
        }
    }

    // Stable identity for fusing/de-duplicating a SearchResult across the lexical and dense arms.
    private static string FusionKey(SearchResult result) => result.Type switch
    {
        "column" => $"c|{result.DataSourceId}|{result.SchemaName}|{result.TableName}|{result.ColumnName}",
        "table" => $"t|{result.DataSourceId}|{result.SchemaName}|{result.TableName}",
        _ => $"{result.Type}|{result.DataSourceName}|{result.Description}"
    };

    // Dense (semantic) arm: embed the query once, pull the nearest table/column embeddings for the data
    // source, and map them back to SearchResults preserving the vector-similarity ranking. PostgreSQL
    // uses the pgvector HNSW index via a raw <=> query; other providers do in-memory cosine over the
    // stored bytes (metadata-scale catalogs). Exemplar embeddings are excluded — those feed few-shot (B6).
    private async Task<List<SearchResult>> BuildDenseResultsAsync(
        BeaconContext context, string query, int dataSourceId, int limit, CancellationToken ct)
    {
        var queryVector = await embeddingService.EmbedAsync(query, ct);

        var hits = context.Database.ProviderName == NpgsqlProviderName
            ? await GetNearestPostgresAsync(context, queryVector, dataSourceId, MetadataOwnerTypes, limit, ct)
            : await GetNearestInMemoryAsync(context, queryVector, dataSourceId, MetadataOwnerTypes, limit, ct);

        if (hits.Count == 0)
        {
            return [];
        }

        var tableOwnerIds = hits
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.MetadataTable)
            .Select(x => x.OwnerId)
            .ToList();
        var columnOwnerIds = hits
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.MetadataColumn)
            .Select(x => x.OwnerId)
            .ToList();

        var tablesById = new Dictionary<int, SearchResult>();
        if (tableOwnerIds.Count > 0)
        {
            var tableRows = await context.DatabaseMetadata
                .Where(x => tableOwnerIds.Contains(x.Id))
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.DataSourceId,
                        DataSourceName = x.DataSource.Name,
                        x.SchemaName,
                        x.TableName,
                        x.TableDescription
                    })
                .ToListAsync(ct);

            foreach (var row in tableRows)
            {
                tablesById[row.Id] = new SearchResult
                {
                    Type = "table",
                    DataSourceId = row.DataSourceId,
                    DataSourceName = row.DataSourceName,
                    SchemaName = row.SchemaName,
                    TableName = row.TableName,
                    Description = row.TableDescription,
                    Relevance = 0
                };
            }
        }

        var columnsById = new Dictionary<int, SearchResult>();
        if (columnOwnerIds.Count > 0)
        {
            var columnRows = await context.ColumnMetadata
                .Where(x => columnOwnerIds.Contains(x.Id))
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.DatabaseMetadata.DataSourceId,
                        DataSourceName = x.DatabaseMetadata.DataSource.Name,
                        x.DatabaseMetadata.SchemaName,
                        x.DatabaseMetadata.TableName,
                        x.ColumnName,
                        x.Description
                    })
                .ToListAsync(ct);

            foreach (var row in columnRows)
            {
                columnsById[row.Id] = new SearchResult
                {
                    Type = "column",
                    DataSourceId = row.DataSourceId,
                    DataSourceName = row.DataSourceName,
                    SchemaName = row.SchemaName,
                    TableName = row.TableName,
                    ColumnName = row.ColumnName,
                    Description = row.Description,
                    Relevance = 0
                };
            }
        }

        // Re-project hits into SearchResults in the vector-similarity order returned above; Relevance is a
        // placeholder here (0) — RRF assigns the final fused score once this arm is fused with the lexical one.
        var dense = new List<SearchResult>();
        foreach (var hit in hits)
        {
            if (hit.OwnerType == McpEmbeddingOwnerType.MetadataTable
                && tablesById.TryGetValue(hit.OwnerId, out var tableResult))
            {
                dense.Add(tableResult);
            }
            else if (hit.OwnerType == McpEmbeddingOwnerType.MetadataColumn
                && columnsById.TryGetValue(hit.OwnerId, out var columnResult))
            {
                dense.Add(columnResult);
            }
        }

        return dense;
    }

    private static async Task<List<EmbeddingHit>> GetNearestPostgresAsync(
        BeaconContext context, float[] queryVector, int dataSourceId, int[] ownerTypes, int limit, CancellationToken ct)
    {
        // The query vector is our own floats, formatted invariantly, but still passed as a parameter
        // (never string-concatenated) and cast to vector(384) so pgvector can use the <=> HNSW index (§1.10).
        var vectorLiteral = "[" + string.Join(",", queryVector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

        // Only the EF-mapped columns are selected — the DB-managed `embedding` vector column is left out so
        // Npgsql never has to read the vector type (no Pgvector handler is registered on this data source).
        // ownerTypes is our own enum-constant array (0/1 metadata, 2 exemplar), passed as a single
        // parameterized integer[] via owner_type = ANY(...) — never string-interpolated (§1.10).
        FormattableString sql = $@"
            SELECT id, data_source_id, owner_type, owner_id, embedding_bytes, model, dimensions, embedding_version, created_time
            FROM mcp_embeddings
            WHERE data_source_id = {dataSourceId}
              AND owner_type = ANY({ownerTypes})
            ORDER BY embedding <=> {vectorLiteral}::vector(384)
            LIMIT {limit}";

        // No LINQ composition on top of FromSqlInterpolated so the raw ORDER BY / LIMIT run verbatim.
        var rows = await context.McpEmbeddings
            .FromSqlInterpolated(sql)
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .Select(x => new EmbeddingHit(x.OwnerType, x.OwnerId))
            .ToList();
    }

    private static async Task<List<EmbeddingHit>> GetNearestInMemoryAsync(
        BeaconContext context, float[] queryVector, int dataSourceId, int[] ownerTypes, int limit, CancellationToken ct)
    {
        var ownerTypeFilter = ownerTypes
            .Select(x => (McpEmbeddingOwnerType)x)
            .ToList();

        var candidates = await context.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => ownerTypeFilter.Contains(x.OwnerType))
            .Select(x =>
                new
                {
                    x.OwnerType,
                    x.OwnerId,
                    x.EmbeddingBytes
                })
            .ToListAsync(ct);

        var expectedBytes = queryVector.Length * sizeof(float);

        return candidates
            .Where(x => x.EmbeddingBytes.Length == expectedBytes)
            .Select(x =>
                new
                {
                    x.OwnerType,
                    x.OwnerId,
                    Score = EmbeddingCodec.Cosine(queryVector, EmbeddingCodec.FromBytes(x.EmbeddingBytes))
                })
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => new EmbeddingHit(x.OwnerType, x.OwnerId))
            .ToList();
    }

    private readonly record struct EmbeddingHit(McpEmbeddingOwnerType OwnerType, int OwnerId);

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
                    c.ForeignKeyTable, c.ForeignKeyColumn, c.Description,
                    c.MaxLength, c.SampleValues
                )).ToList()
            })
            .ToListAsync(ct);

        var totalColumns = allTables.Sum(t => t.Columns.Count);
        var catalog = BuildSchemaCatalog(allTables.Select(t => (t.SchemaName, t.TableName, (IEnumerable<SchemaColumn>)t.Columns)));
        var dialect = dataSource.DatabaseEngineType?.ToString();
        var mcpSettings = await settingsProvider.GetSettingsAsync(ct);

        // Small schema fast path: send everything
        if (allTables.Count <= 40 && totalColumns <= 300)
        {
            var sb = new StringBuilder();
            sb.AppendLine(isApi
                ? $"# REST API: {dataSource.Name}"
                : $"# Data Source: {dataSource.Name} ({dataSource.DatabaseEngineType})");
            sb.AppendLine($"Tables: {allTables.Count}\n");

            foreach (var t in allTables)
                SchemaContextFormatter.AppendTableWithFullColumns(sb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);

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
            SchemaContextFormatter.AppendTableWithFullColumns(smartSb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);

        // Section 2: Other tables compact
        var otherTables = allTables.Where(t => !detailedTables.Contains($"{t.SchemaName}.{t.TableName}")).ToList();
        if (otherTables.Count > 0)
        {
            smartSb.AppendLine("\n## Other Tables (name and primary keys only)\n");
            foreach (var t in otherTables)
                SchemaContextFormatter.AppendTableCompact(smartSb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);
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
        var nameList = tableNames
            .Select(x => x.ToLower())
            .Distinct()
            .ToList();

        // Match by table name or schema.table, case-insensitive, filtered in SQL so only the
        // requested tables (not the whole catalog) are materialized.
        var matched = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .Where(m => nameList.Contains(m.TableName.ToLower())
                || nameList.Contains((m.SchemaName + "." + m.TableName).ToLower()))
            .Select(m => new
            {
                m.SchemaName,
                m.TableName,
                m.TableDescription,
                Columns = m.Columns.Select(c => new SchemaColumn(
                    c.ColumnName, c.DataType, c.IsPrimaryKey, c.IsNullable,
                    c.ForeignKeyTable, c.ForeignKeyColumn, c.Description,
                    c.MaxLength, c.SampleValues
                )).ToList()
            })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("# Table Schemas\n");
        foreach (var t in matched)
            SchemaContextFormatter.AppendTableWithFullColumns(sb, t.SchemaName, t.TableName, t.TableDescription, t.Columns, isApi);

        return sb.ToString();
    }

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

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "was", "were", "how", "many", "much", "what", "which",
        "who", "whom", "with", "from", "into", "that", "this", "these", "those", "have",
        "has", "had", "did", "does", "all", "any", "each", "per", "show", "list", "give",
        "get", "find", "count", "last", "week", "month", "year", "day", "between", "over"
    };

    private static List<string> TokenizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return System.Text.RegularExpressions.Regex
            .Split(query, "[^A-Za-z0-9]+")
            .Where(x => x.Length > 2)
            .Select(x => x.ToLowerInvariant())
            .Where(x => !StopWords.Contains(x))
            .Distinct()
            .ToList();
    }

    private static int CountOverlap(List<string> terms, string name, string? description)
    {
        var nameLower = name.ToLowerInvariant();
        var descLower = description?.ToLowerInvariant();
        var count = 0;
        foreach (var term in terms)
        {
            if (nameLower.Contains(term) || (descLower != null && descLower.Contains(term)))
            {
                count++;
            }
        }

        return count;
    }

    public async Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(
        int dataSourceId, List<string> tableNames, string? question = null,
        int maxPatterns = 10, int budgetChars = 1500, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var lowerTables = tableNames
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();

        // The approved/auto-approved pattern bank feeds BOTH paths: semantic selection picks the top-k
        // nearest lessons of ANY type out of it by embedding similarity, and the table-overlap ranking
        // orders the rest. Stale (superseded) lessons are filtered out here so they are NEVER injected
        // into a prompt on either path (§ Architecture ⑧ temporal decay) — history stays in the table.
        var patterns = await context.McpLearnedPatterns
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.SupersededAt == null)
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.SignalCount)
            .Take(100)
            .Select(x =>
                new PatternCandidate
                {
                    Id = x.Id,
                    PatternType = x.PatternType,
                    TableName = x.TableName,
                    SchemaName = x.SchemaName,
                    ColumnName = x.ColumnName,
                    PatternContent = x.PatternContent,
                    ExampleQuestion = x.ExampleQuestion,
                    ExampleSql = x.ExampleSql,
                    Confidence = x.Confidence
                })
            .ToListAsync(ct);

        var settings = await settingsProvider.GetSettingsAsync(ct);

        // DAIL-SQL semantic selection (§ Architecture ⑧): choose the top-k nearest lessons of ANY type by
        // masked-question embedding similarity instead of blanket-injecting the whole bank. Lessons that have
        // no embedding yet (e.g. not re-indexed) fall back to the table-overlap ranking and are merged in
        // after the semantic picks. Any failure falls back to the full overlap ranking over all types.
        var useSemantic = embeddingService.IsAvailable
            && settings.EnableSemanticRetrieval
            && !string.IsNullOrWhiteSpace(question);

        if (useSemantic)
        {
            try
            {
                var semanticRanked = await BuildSemanticRankingAsync(
                    context, patterns, lowerTables, question!, dataSourceId, settings.ExemplarTopK, ct);
                if (semanticRanked != null)
                {
                    return ApplyBudget(semanticRanked, maxPatterns, budgetChars);
                }
                // No exemplar vectors indexed / none mapped back — fall through to overlap behaviour.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Semantic exemplar selection failed for data source {DataSourceId}; falling back to table-overlap pattern ranking.", dataSourceId);
            }
        }

        // Behaviour-preserving fallback: the historical table-overlap ranking over ALL pattern types.
        return ApplyBudget(RankByTableOverlap(patterns, lowerTables), maxPatterns, budgetChars);
    }

    // Semantic path (§ Architecture ⑧): embed the masked question once, pull the nearest Exemplar embeddings
    // for this data source, and map their OwnerIds back to the loaded valid bank of ANY pattern type in
    // vector-similarity order — these top-k picks lead. Lessons that have NO exemplar embedding (e.g. added
    // since the last re-index, so they could not be ranked semantically) keep the table-overlap ranking and
    // follow, so they stay visible rather than silently disappearing. Lessons that DO have an embedding but
    // were not in the top-k are intentionally dropped — that is the point of top-k over blanket injection.
    // Returns null when no exemplar vector maps back to a loaded pattern, so the caller falls back to full
    // overlap ranking.
    private async Task<List<PatternCandidate>?> BuildSemanticRankingAsync(
        BeaconContext context, List<PatternCandidate> patterns, HashSet<string> lowerTables,
        string question, int dataSourceId, int exemplarTopK, CancellationToken ct)
    {
        var topK = Math.Max(1, exemplarTopK);
        var maskedVector = await embeddingService.EmbedAsync(EmbeddingMaskingHelper.Mask(question), ct);

        var hits = context.Database.ProviderName == NpgsqlProviderName
            ? await GetNearestPostgresAsync(context, maskedVector, dataSourceId, ExemplarOwnerTypes, topK, ct)
            : await GetNearestInMemoryAsync(context, maskedVector, dataSourceId, ExemplarOwnerTypes, topK, ct);

        if (hits.Count == 0)
        {
            return null;
        }

        // Exemplar OwnerId == McpLearnedPattern.Id. Map hits (already in similarity order) back to the loaded
        // bank of ALL types, preserving that order; ignore ids not in the bank (e.g. a hit whose pattern was
        // superseded and filtered out of the bank above).
        var patternsById = patterns.ToDictionary(x => x.Id);

        var selected = new List<PatternCandidate>();
        var selectedIds = new HashSet<int>();
        foreach (var hit in hits)
        {
            if (patternsById.TryGetValue(hit.OwnerId, out var pattern) && selectedIds.Add(hit.OwnerId))
            {
                selected.Add(pattern);
            }
        }

        if (selected.Count == 0)
        {
            return null;
        }

        // A lesson with an exemplar embedding was a candidate for the top-k; if it was not selected it is
        // deliberately dropped. A lesson WITHOUT an embedding could not be ranked semantically, so it falls
        // back to the table-overlap ranking and is merged in after the semantic picks (one cheap id lookup —
        // no vectors loaded). This keeps freshly-added, not-yet-indexed lessons visible.
        var embeddedIds = await context.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar)
            .Select(x => x.OwnerId)
            .ToListAsync(ct);
        var embeddedIdSet = embeddedIds.ToHashSet();

        var backfill = RankByTableOverlap(
            patterns.Where(x => !selectedIds.Contains(x.Id) && !embeddedIdSet.Contains(x.Id)),
            lowerTables);

        return selected
            .Concat(backfill)
            .ToList();
    }

    // Historical relevance ranking: table overlap (biggest lever) + pattern-type priority + confidence.
    private static List<PatternCandidate> RankByTableOverlap(
        IEnumerable<PatternCandidate> patterns, HashSet<string> lowerTables)
    {
        return patterns
            .Select(x =>
            {
                var tableFull = $"{x.SchemaName}.{x.TableName}".ToLowerInvariant();
                var tableOnly = x.TableName.ToLowerInvariant();
                var tableMatch = lowerTables.Contains(tableFull) || lowerTables.Contains(tableOnly);
                var typePriority = x.PatternType switch
                {
                    McpPatternType.SchemaCorrection => 4,
                    McpPatternType.ColumnClarification => 3,
                    McpPatternType.JoinPattern => 2,
                    McpPatternType.CommonQuery => 1,
                    _ => 0
                };
                var score = (tableMatch ? 10 : 0) + typePriority + x.Confidence;
                return (Pattern: x, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Pattern)
            .ToList();
    }

    // Shared budget cap: take patterns in the supplied order until maxPatterns or budgetChars is hit.
    private static List<LearnedPatternInfo> ApplyBudget(
        List<PatternCandidate> ranked, int maxPatterns, int budgetChars)
    {
        var result = new List<LearnedPatternInfo>();
        var totalChars = 0;

        foreach (var x in ranked)
        {
            if (result.Count >= maxPatterns)
            {
                break;
            }

            var contentLen = x.PatternContent.Length + (x.ExampleSql?.Length ?? 0) + 20;
            if (totalChars + contentLen > budgetChars && result.Count > 0)
            {
                break;
            }

            totalChars += contentLen;
            result.Add(new LearnedPatternInfo(
                x.PatternType.ToString(),
                x.TableName,
                x.ColumnName,
                x.PatternContent,
                x.ExampleQuestion,
                x.ExampleSql,
                x.Confidence));
        }

        return result;
    }

    // Materialized pattern-bank row shared by the semantic and overlap ranking paths.
    private sealed record PatternCandidate
    {
        public int Id { get; init; }
        public McpPatternType PatternType { get; init; }
        public string TableName { get; init; } = null!;
        public string SchemaName { get; init; } = null!;
        public string? ColumnName { get; init; }
        public string PatternContent { get; init; } = null!;
        public string? ExampleQuestion { get; init; }
        public string? ExampleSql { get; init; }
        public double Confidence { get; init; }
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
