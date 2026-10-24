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

internal sealed partial class KnowledgeGraphService
{
    public async Task<List<SearchResult>> SearchAsync(string query, int projectId, int? dataSourceId = null, int maxResults = 20, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Only the data sources the caller's project links are searched (§1.12), on both arms: a named data source the
        // project does not link yields nothing.
        var projectSourceIds = ProjectSourceIds(context, projectId);
        if (dataSourceId.HasValue && !await projectSourceIds.ContainsAsync(dataSourceId.Value, ct))
        {
            return [];
        }

        var results = new List<SearchResult>();
        var queryLower = query.ToLower();

        // Tokenize the question so each meaningful term can match independently. Searching
        // for the whole question as a single substring never matches on real schemas.
        var terms = TokenizeQuery(query);

        // Fall back to the whole (lowercased) query when tokenization yields nothing useful.
        if (terms.Count == 0)
            terms = [queryLower];

        // Search tables by name or description — match ANY term.
        var tablesQuery = context.DatabaseMetadata
            .Where(m => projectSourceIds.Contains(m.DataSourceId));
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
        var columnsQuery = context.ColumnMetadata
            .Where(c => projectSourceIds.Contains(c.DatabaseMetadata.DataSourceId));
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
            logger.LogWarning("Dense retrieval arm failed for data source {DataSourceId}; falling back to lexical search ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
            return lexicalRanked.Take(maxResults).ToList();
        }
    }

    // Stable identity for fusing/de-duplicating a SearchResult across the lexical and dense arms.
    // Documentation results dedupe on their per-row DocIdentity when present — the truncated preview
    // text alone collapses two distinct docs sharing a 200-char prefix into one result.
    private static string FusionKey(SearchResult result) => result.Type switch
    {
        "column" => $"c|{result.DataSourceId}|{result.SchemaName}|{result.TableName}|{result.ColumnName}",
        "table" => $"t|{result.DataSourceId}|{result.SchemaName}|{result.TableName}",
        _ when result.DocIdentity != null => $"{result.Type}|{result.DocIdentity}",
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
            ? await GetNearestPostgresAsync(context, queryVector, [dataSourceId], MetadataOwnerTypes, limit, ct, projectId: null)
            : await GetNearestInMemoryAsync(context, queryVector, [dataSourceId], MetadataOwnerTypes, limit, ct, projectId: null);

        return await MapMetadataHitsToResultsAsync(context, hits, ct);
    }

    // MetadataTable OwnerId == DatabaseMetadata.Id; MetadataColumn OwnerId == ColumnMetadata.Id. Maps
    // hits back to table/column SearchResults preserving the vector-similarity order. Shared by the
    // data-source dense arm (SearchAsync) and the project semantic arm (SearchProjectAsync).
    private static async Task<List<SearchResult>> MapMetadataHitsToResultsAsync(
        BeaconContext context, List<EmbeddingHit> hits, CancellationToken ct)
    {
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
        BeaconContext context, float[] queryVector, int[] dataSourceIds, int[] ownerTypes, int limit, CancellationToken ct, int? projectId = null)
    {
        // The query vector is our own floats, formatted invariantly, but still passed as a parameter
        // (never string-concatenated) and cast to vector(384) so pgvector can use the <=> HNSW index (§1.10).
        var vectorLiteral = "[" + string.Join(",", queryVector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

        // Only the EF-mapped columns are selected — the DB-managed `embedding` vector column is left out so
        // Npgsql never has to read the vector type (no Pgvector handler is registered on this data source).
        // ownerTypes and dataSourceIds are our own arrays, each passed as a single parameterized integer[]
        // via `= ANY(...)` — never string-interpolated (§1.10). Scope selection:
        // - projectId alone (empty dataSourceIds): project-scoped rows like doc-chunks/glossary — the
        //   filter is project_id INSTEAD OF data_source_id.
        // - dataSourceIds alone: data-source-scoped metadata rows; the project semantic arm can scan all
        //   of a project's data sources in one globally-ranked kNN (single-source callers pass a
        //   one-element array).
        // - BOTH: exemplar retrieval (R5/C1) — data_source_id AND project_id, so a shared data source
        //   never surfaces another project's exemplar vectors in the top-k.
        // All branches stay fully parameterized and select every mapped McpEmbedding column
        // (project_id added in B1) so FromSqlInterpolated can materialize the entity.
        FormattableString sql;
        if (projectId.HasValue && dataSourceIds.Length > 0)
        {
            sql = $@"
                SELECT id, data_source_id, project_id, owner_type, owner_id, embedding_bytes, model, dimensions, embedding_version, created_time
                FROM mcp_embeddings
                WHERE data_source_id = ANY({dataSourceIds})
                  AND project_id = {projectId.Value}
                  AND owner_type = ANY({ownerTypes})
                ORDER BY embedding <=> {vectorLiteral}::vector(384)
                LIMIT {limit}";
        }
        else if (projectId.HasValue)
        {
            sql = $@"
                SELECT id, data_source_id, project_id, owner_type, owner_id, embedding_bytes, model, dimensions, embedding_version, created_time
                FROM mcp_embeddings
                WHERE project_id = {projectId.Value}
                  AND owner_type = ANY({ownerTypes})
                ORDER BY embedding <=> {vectorLiteral}::vector(384)
                LIMIT {limit}";
        }
        else
        {
            sql = $@"
                SELECT id, data_source_id, project_id, owner_type, owner_id, embedding_bytes, model, dimensions, embedding_version, created_time
                FROM mcp_embeddings
                WHERE data_source_id = ANY({dataSourceIds})
                  AND owner_type = ANY({ownerTypes})
                ORDER BY embedding <=> {vectorLiteral}::vector(384)
                LIMIT {limit}";
        }

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
        BeaconContext context, float[] queryVector, int[] dataSourceIds, int[] ownerTypes, int limit, CancellationToken ct, int? projectId = null)
    {
        var ownerTypeFilter = ownerTypes
            .Select(x => (McpEmbeddingOwnerType)x)
            .ToList();

        // Scope selection mirrors the Postgres branch: project_id alone for project-scoped
        // doc-chunk/glossary rows; data_source_id alone for metadata rows (an id set, so the project
        // semantic arm can scan all of a project's data sources; single-source callers pass one
        // element); BOTH for exemplar retrieval (R5/C1) so a shared data source never surfaces
        // another project's exemplar vectors in the top-k.
        IQueryable<McpEmbedding> scoped = context.McpEmbeddings;
        if (projectId.HasValue)
        {
            scoped = scoped.Where(x => x.ProjectId == projectId);
        }

        if (!projectId.HasValue || dataSourceIds.Length > 0)
        {
            scoped = scoped
                .Where(x => x.DataSourceId != null)
                .Where(x => dataSourceIds.Contains(x.DataSourceId!.Value));
        }

        // Rows stream through a bounded top-k selection: the vectors are scored where they arrive, so the scope's
        // bytes are never all held at once.
        var rows = scoped
            .Where(x => ownerTypeFilter.Contains(x.OwnerType))
            .Select(x =>
                new
                {
                    x.OwnerType,
                    x.OwnerId,
                    x.EmbeddingBytes
                })
            .AsAsyncEnumerable();

        var nearest = await NearestEmbeddingSelector.TopKAsync(rows, x => x.EmbeddingBytes, queryVector, limit, ct);

        return nearest
            .Select(x => new EmbeddingHit(x.OwnerType, x.OwnerId))
            .ToList();
    }

    private readonly record struct EmbeddingHit(McpEmbeddingOwnerType OwnerType, int OwnerId);

    public async Task<List<SearchResult>> SearchProjectAsync(string query, int projectId, int maxResults = 20, CancellationToken ct = default)
    {
        var terms = SearchTermsOf(query);
        if (terms.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Get all data source IDs for this project
        var dsIds = await context.ProjectDataSources
            .WhereReadable()
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        var results = new List<SearchResult>();
        var phrase = query.Trim();
        var phraseLower = phrase.ToLowerInvariant();

        // Search tables (a project with only imported documents has no data sources — skip the catalog queries)
        var matchingTables = dsIds.Count == 0
            ? []
            : await BuildMatchingTablesQuery(context, dsIds, terms, maxResults)
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
                Relevance = (table.TableName.Equals(phrase, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.8) * TermShare(table.Hits, terms)
            });
        }

        // Search columns
        var matchingColumns = dsIds.Count == 0
            ? []
            : await BuildMatchingColumnsQuery(context, dsIds, terms, maxResults)
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
                Relevance = (col.ColumnName.Equals(phrase, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.6) * TermShare(col.Hits, terms)
            });
        }

        // Search project documentation
        var docSections = await BuildDocSectionsQuery(context, projectId, terms, maxResults / 2)
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
                Relevance = 0.5 * TermShare(doc.Hits, terms),
                DocIdentity = $"docsection:{doc.Id}",
                SectionType = doc.SectionType
            });
        }

        // Search host-imported documents (ExposeDocs) — title/path, or any of their chunks. Works with no embedder.
        var importedDocs = await BuildImportedDocsQuery(context, projectId, terms, maxResults / 2)
            .ToListAsync(ct);

        foreach (var doc in importedDocs)
        {
            results.Add(new SearchResult
            {
                Type = "imported_doc",
                DataSourceName = doc.ProjectName,
                SchemaName = string.Empty,
                TableName = string.Empty,
                Description = doc.MatchingChunk != null ? TruncateContent(doc.MatchingChunk, 200) : null,
                Relevance = (doc.Title.ToLowerInvariant().Contains(phraseLower) ? 0.55 : 0.5) * TermShare(doc.Hits, terms),
                DocIdentity = $"importeddoc:{doc.Id}",
                DocumentPath = doc.Path,
                DocumentTitle = doc.Title
            });
        }

        // Keyword arm — exactly the result returned before the semantic arm existed, and the
        // behaviour-preserving fallback whenever embeddings are unavailable or the semantic arm fails.
        var keywordRanked = OrderForDeterministicPaging(results, maxResults);

        return await ApplyProjectSemanticFusionAsync(context, keywordRanked, query, projectId, dsIds, maxResults, ct);
    }

    /// <summary>
    /// Semantic arm + fusion for project search (the MCP <c>search</c> tool). Fully fail-open: any gate
    /// not satisfied (no local embedder, semantic retrieval disabled, no vectors indexed) or ANY error in
    /// the semantic arm returns <paramref name="keywordRanked"/> unchanged, so the tool behaves exactly as
    /// keyword-only search did. Mirrors the fail-open discipline of the grounding blocks
    /// (<see cref="BuildGlossaryBlockAsync"/> / <see cref="BuildGoldenExemplarBlockAsync"/>): rethrow OCE
    /// so a shutdown/timeout unwinds; otherwise log identifiers only (§1.11 — never the query text) and
    /// fall back. Internal so SearchFusionTests exercise the REAL fail-open path.
    /// </summary>
    internal async Task<List<SearchResult>> ApplyProjectSemanticFusionAsync(
        BeaconContext context,
        List<SearchResult> keywordRanked,
        string query,
        int projectId,
        List<int> dsIds,
        int maxResults,
        CancellationToken ct)
    {
        if (!embeddingService.IsAvailable)
        {
            return keywordRanked;
        }

        try
        {
            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
            if (!settings.EnableSemanticRetrieval)
            {
                return keywordRanked;
            }

            var semanticRanked = await BuildProjectSemanticArmAsync(context, query, projectId, dsIds, maxResults * 2, ct);
            if (semanticRanked.Count == 0)
            {
                // No vectors indexed yet (or nothing matched) — nothing to fuse; keep keyword behaviour.
                return keywordRanked;
            }

            return FuseProjectSearchArms(keywordRanked, semanticRanked, maxResults);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Semantic search arm failed for project {ProjectId}; returning keyword-only results ({Failure}).", projectId, FailureCodes.Describe(ex));
            return keywordRanked;
        }
    }

    // Fuses the keyword-ranked and semantic-ranked arms via RRF, re-weights Relevance with the fused
    // score, and re-applies the deterministic paging order: fused score (as Relevance) descending, then
    // the existing name tie-breaks — equal fused scores must not reshuffle between calls.
    internal static List<SearchResult> FuseProjectSearchArms(
        IReadOnlyList<SearchResult> keywordRanked,
        IReadOnlyList<SearchResult> semanticRanked,
        int maxResults)
    {
        var fused = ReciprocalRankFusion.FuseWithScores(
            new[] { keywordRanked, semanticRanked },
            FusionKey);

        return OrderForDeterministicPaging(
            fused.Select(x => x.Item with { Relevance = x.Score }),
            maxResults);
    }

    // Dense (semantic) arm for project search: embed the RAW keyword once — metadata and doc chunks are
    // embedded RAW at index time (BuildTableText/BuildColumnText, blurb + chunk); masking is a question-
    // specific exemplar/golden-case convention and would move an arbitrary keyword into a different
    // representation region. Then kNN over the project's data-source-scoped table/column embeddings and
    // its project-scoped doc-chunk embeddings, mapping hits back to the same SearchResult shapes the
    // keyword arm produces. Doc-chunk hits follow the metadata hits: the two kNN calls return rank-only
    // hits with no scores comparable across scopes, and the keyword arm ranks documentation below
    // tables/columns the same way. Exemplar, GoldenCase and GlossaryTerm embeddings are deliberately
    // excluded — they ground SQL generation and have no table/column/doc identity in the result shape.
    private async Task<List<SearchResult>> BuildProjectSemanticArmAsync(
        BeaconContext context, string query, int projectId, List<int> dsIds, int limit, CancellationToken ct)
    {
        var queryVector = await embeddingService.EmbedAsync(query, ct);
        var dataSourceIds = dsIds.ToArray();

        var metadataHits = context.Database.ProviderName == NpgsqlProviderName
            ? await GetNearestPostgresAsync(context, queryVector, dataSourceIds, MetadataOwnerTypes, limit, ct, projectId: null)
            : await GetNearestInMemoryAsync(context, queryVector, dataSourceIds, MetadataOwnerTypes, limit, ct, projectId: null);

        var semantic = await MapMetadataHitsToResultsAsync(context, metadataHits, ct);

        var docLimit = Math.Max(1, limit / 2);
        var docHits = context.Database.ProviderName == NpgsqlProviderName
            ? await GetNearestPostgresAsync(context, queryVector, [], DocChunkOwnerTypes, docLimit, ct, projectId: projectId)
            : await GetNearestInMemoryAsync(context, queryVector, [], DocChunkOwnerTypes, docLimit, ct, projectId: projectId);

        semantic.AddRange(await MapDocChunkHitsToResultsAsync(context, docHits, projectId, ct));

        return semantic;
    }

    // DocChunk OwnerId == McpDocChunk.Id. Maps hits back to "documentation" results in the shape the
    // keyword arm produces (project name as the source, truncated chunk text as the description),
    // preserving the vector-similarity order. Relevance is a placeholder (0) — RRF assigns the fused score.
    // DocIdentity uses the chunk's SOURCE SECTION id ("docsection:{SourceSectionId}") — the same
    // namespace the keyword arm emits for ProjectDocumentationSection rows — so a section retrieved by
    // BOTH arms fuses into one result instead of appearing twice under disjoint identities. Two chunks
    // of the SAME section consequently collapse too: fusion granularity for documentation is the
    // section, matching what the keyword arm returns.
    private static async Task<List<SearchResult>> MapDocChunkHitsToResultsAsync(
        BeaconContext context, List<EmbeddingHit> hits, int projectId, CancellationToken ct)
    {
        if (hits.Count == 0)
        {
            return [];
        }

        var projectName = await context.Projects
            .Where(x => x.Id == projectId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(ct);

        if (projectName == null)
        {
            return [];
        }

        var ownerIds = hits
            .Select(x => x.OwnerId)
            .ToList();

        var chunks = await context.McpDocChunks
            .Where(x => x.ProjectId == projectId)
            .Where(x => ownerIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.SourceSectionId,
                    x.ChunkText,
                    x.ImportedDocumentId,
                    ImportedPath = x.ImportedDocument != null ? x.ImportedDocument.Path : null,
                    ImportedTitle = x.ImportedDocument != null ? x.ImportedDocument.Title : null
                })
            .ToListAsync(ct);

        var chunksById = chunks.ToDictionary(x => x.Id);

        // A chunk of generated documentation carries its section's type, which says whether its text can list values
        // discovered in the data; an imported document's chunk has no section.
        var sectionIds = chunks
            .Select(x => x.SourceSectionId)
            .Where(x => x != 0)
            .Distinct()
            .ToList();
        var sectionTypes = sectionIds.Count == 0
            ? new Dictionary<int, ProjectDocSectionType>()
            : await context.ProjectDocumentationSections
                .Where(x => sectionIds.Contains(x.Id))
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.SectionType
                    })
                .ToDictionaryAsync(x => x.Id, x => x.SectionType, ct);

        var results = new List<SearchResult>();
        foreach (var hit in hits)
        {
            if (!chunksById.TryGetValue(hit.OwnerId, out var chunk))
            {
                continue;
            }

            // A host-imported document chunk: identity is the document, so both arms fuse per document.
            if (chunk.ImportedDocumentId != null)
            {
                if (chunk.ImportedPath != null)
                {
                    results.Add(new SearchResult
                    {
                        Type = "imported_doc",
                        DataSourceName = projectName,
                        SchemaName = string.Empty,
                        TableName = string.Empty,
                        Description = TruncateContent(chunk.ChunkText, 200),
                        Relevance = 0,
                        DocIdentity = $"importeddoc:{chunk.ImportedDocumentId}",
                        DocumentPath = chunk.ImportedPath,
                        DocumentTitle = chunk.ImportedTitle
                    });
                }

                continue;
            }

            results.Add(new SearchResult
            {
                Type = "documentation",
                DataSourceName = projectName,
                SchemaName = string.Empty,
                TableName = string.Empty,
                Description = TruncateContent(chunk.ChunkText, 200),
                Relevance = 0,
                DocIdentity = $"docsection:{chunk.SourceSectionId}",
                SectionType = sectionTypes.TryGetValue(chunk.SourceSectionId, out var sectionType) ? sectionType : null
            });
        }

        return results;
    }

    // SearchProjectAsync sub-queries, exposed as IQueryable builders so translation tests can run
    // ToQueryString() on the REAL query shapes (InternalsVisibleTo Beacon.Tests). A row matches when it contains any of
    // the search terms (SearchTermsOf); Hits counts the terms it contains and is the first sort key, so the rows that hit
    // the most terms survive the Take. Projections target named row records instead of anonymous types — EF projects
    // into the ctor identically. Ordering sits BEFORE the final projection because EF cannot compose OrderBy over
    // ctor-projected members.
    internal static IQueryable<TableSearchRow> BuildMatchingTablesQuery(BeaconContext context, List<int> dsIds, List<string> terms, int maxResults)
    {
        return context.DatabaseMetadata
            .Where(x => dsIds.Contains(x.DataSourceId))
            .Select(x =>
                new
                {
                    Table = x,
                    Hits = terms.Count(y => x.TableName.ToLower().Contains(y) ||
                                            (x.TableDescription != null && x.TableDescription.ToLower().Contains(y)))
                })
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Table.SchemaName)
            .ThenBy(x => x.Table.TableName)
            .ThenBy(x => x.Table.DataSourceId)
            .ThenBy(x => x.Table.Id)
            .Select(x =>
                new TableSearchRow(
                    x.Table.DataSourceId,
                    x.Table.DataSource.Name,
                    x.Table.SchemaName,
                    x.Table.TableName,
                    x.Table.TableDescription,
                    x.Hits))
            .Take(maxResults);
    }

    internal static IQueryable<ColumnSearchRow> BuildMatchingColumnsQuery(BeaconContext context, List<int> dsIds, List<string> terms, int maxResults)
    {
        return context.ColumnMetadata
            .Where(x => dsIds.Contains(x.DatabaseMetadata.DataSourceId))
            .Select(x =>
                new
                {
                    Column = x,
                    Hits = terms.Count(y => x.ColumnName.ToLower().Contains(y) ||
                                            (x.Description != null && x.Description.ToLower().Contains(y)))
                })
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Column.DatabaseMetadata.SchemaName)
            .ThenBy(x => x.Column.DatabaseMetadata.TableName)
            .ThenBy(x => x.Column.ColumnName)
            .ThenBy(x => x.Column.DatabaseMetadata.DataSourceId)
            .ThenBy(x => x.Column.Id)
            .Select(x =>
                new ColumnSearchRow(
                    x.Column.DatabaseMetadata.DataSourceId,
                    x.Column.DatabaseMetadata.DataSource.Name,
                    x.Column.DatabaseMetadata.SchemaName,
                    x.Column.DatabaseMetadata.TableName,
                    x.Column.ColumnName,
                    x.Column.Description,
                    x.Hits))
            .Take(maxResults);
    }

    internal static IQueryable<DocSectionSearchRow> BuildDocSectionsQuery(BeaconContext context, int projectId, List<string> terms, int maxResults)
    {
        return context.ProjectDocumentationSections
            .Where(x => x.Documentation.ProjectId == projectId)
            .Select(x =>
                new
                {
                    Section = x,
                    Hits = terms.Count(y => x.Content.ToLower().Contains(y))
                })
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Section.Title)
            .ThenBy(x => x.Section.Id)
            .Select(x =>
                new DocSectionSearchRow(
                    x.Section.Id,
                    x.Section.Documentation.Project.Name,
                    x.Section.Title,
                    x.Section.Content,
                    x.Section.SectionType,
                    x.Hits))
            .Take(maxResults);
    }

    // Host-imported documents whose title, path or chunks contain a search term; a term counts once whichever of them
    // holds it. The chunk holding the most terms (the first in document order on a tie) is the result's snippet.
    // Project-scoped (§1.12); archived documents are excluded by the soft-delete filter.
    internal static IQueryable<ImportedDocSearchRow> BuildImportedDocsQuery(BeaconContext context, int projectId, List<string> terms, int maxResults)
    {
        return context.ProjectImportedDocuments
            .Where(x => x.ProjectId == projectId)
            .Select(x =>
                new
                {
                    Document = x,
                    Hits = terms.Count(y => x.Title.ToLower().Contains(y) ||
                                            x.Path.ToLower().Contains(y) ||
                                            context.McpDocChunks
                                                .Where(z => z.ImportedDocumentId == x.Id)
                                                .Any(z => z.ChunkText.ToLower().Contains(y)))
                })
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Document.Path)
            .ThenBy(x => x.Document.Id)
            .Select(x =>
                new ImportedDocSearchRow(
                    x.Document.Id,
                    x.Document.Project.Name,
                    x.Document.Path,
                    x.Document.Title,
                    context.McpDocChunks
                        .Where(y => y.ImportedDocumentId == x.Document.Id)
                        .Select(y =>
                            new
                            {
                                y.ChunkText,
                                y.SortOrder,
                                Hits = terms.Count(z => y.ChunkText.ToLower().Contains(z))
                            })
                        .Where(y => y.Hits > 0)
                        .OrderByDescending(y => y.Hits)
                        .ThenBy(y => y.SortOrder)
                        .Select(y => y.ChunkText)
                        .FirstOrDefault(),
                    x.Hits))
            .Take(maxResults);
    }

    // The search terms of a free-text query: its lower-cased words (letters, digits and underscores, so an identifier
    // like order_items stays one term) without single characters and function words, at most MaxSearchTerms of them.
    // "how do I request annual leave" searches for request, annual and leave. A query with no such word searches for
    // the whole query, as one term; a blank query has none.
    internal static List<string> SearchTermsOf(string query)
    {
        var phrase = query.Trim().ToLowerInvariant();
        if (phrase.Length == 0)
        {
            return [];
        }

        var terms = System.Text.RegularExpressions.Regex
            .Split(phrase, @"[^\p{L}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
            .Where(x => x.Length > 1)
            .Where(x => !SearchStopWords.Contains(x))
            .Distinct()
            .Take(MaxSearchTerms)
            .ToList();

        return terms.Count == 0 ? [phrase] : terms;
    }

    // Deterministic paging: ties inside a coarse relevance band must not reshuffle between calls.
    // DataSourceId disambiguates two data sources sharing a display name, so the chain is unique for
    // table/column results. SearchResult carries no per-row id for documentation results (DataSourceId
    // is 0 and schema/table are empty there), so Description is their last-resort key — two doc
    // sections with identical truncated content remain a residual (harmless) tie.
    internal static List<SearchResult> OrderForDeterministicPaging(IEnumerable<SearchResult> results, int maxResults)
    {
        return results
            .OrderByDescending(x => x.Relevance)
            .ThenBy(x => x.DataSourceName)
            .ThenBy(x => x.DataSourceId)
            .ThenBy(x => x.SchemaName)
            .ThenBy(x => x.TableName)
            .ThenBy(x => x.ColumnName)
            .ThenBy(x => x.DocumentPath)
            .ThenBy(x => x.Description)
            .Take(maxResults)
            .ToList();
    }

    private const int MaxSearchTerms = 8;

    private static readonly HashSet<string> SearchStopWords = new(StringComparer.Ordinal)
    {
        "an", "the", "and", "or", "of", "to", "in", "on", "at", "for", "from", "by", "with", "about", "into", "is",
        "are", "was", "were", "be", "do", "does", "did", "how", "what", "which", "who", "where", "when", "why", "can",
        "could", "should", "would", "will", "me", "my", "we", "our", "you", "your", "it", "its", "this", "that", "there",
        "as", "if"
    };

    // The share of the search terms a row contains, which scales its relevance band: a one-term query keeps the band.
    private static double TermShare(int hits, List<string> terms) => (double)hits / terms.Count;

    // The masked question embedded once per ask, on first use, and shared by the blocks that search by it (verified
    // examples, learned patterns, glossary). A failed embedding fails each of them as it did when each embedded alone.
    private sealed class MaskedQuestionVector(IBeaconEmbeddingService embeddingService, string question)
    {
        private readonly Lock _gate = new();
        private Task<float[]>? _vector;

        public Task<float[]> GetAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                return _vector ??= embeddingService.EmbedAsync(EmbeddingMaskingHelper.Mask(question), ct);
            }
        }
    }
}

// Row shapes of the SearchProjectAsync sub-queries (see the Build*Query methods above).
internal sealed record TableSearchRow(int DataSourceId, string DataSourceName, string SchemaName, string TableName, string? TableDescription, int Hits);

internal sealed record ColumnSearchRow(int DataSourceId, string DataSourceName, string SchemaName, string TableName, string ColumnName, string? Description, int Hits);

internal sealed record DocSectionSearchRow(int Id, string ProjectName, string Title, string Content, ProjectDocSectionType SectionType, int Hits);

internal sealed record ImportedDocSearchRow(int Id, string ProjectName, string Path, string Title, string? MatchingChunk, int Hits);
