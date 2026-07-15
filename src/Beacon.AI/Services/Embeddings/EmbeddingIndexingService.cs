using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Helpers;

namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// Embeds table/column metadata and validated exemplars into <c>McpEmbedding</c> (§ Architecture ②).
/// Runs as a Hangfire recurring job (idempotent per §6.4). Skips entirely when the local embedder is
/// unavailable or when semantic retrieval is disabled, so installs without a model file keep working
/// on the lexical fallback. Mirrors the <c>McpLearningAggregationService</c> split: interface in Core,
/// implementation here, gated by settings, one context per unit of work via <c>IDbContextFactory</c>.
/// </summary>
internal sealed class EmbeddingIndexingService(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconEmbeddingService embeddingService,
    IMcpSettingsProvider settingsProvider,
    IEmbeddingVectorColumnWriter vectorWriter,
    ILogger<EmbeddingIndexingService> logger) : IEmbeddingIndexingService
{
    // The bge-small-en-v1.5 model the local ONNX embedder wraps. Persisted alongside each vector so a
    // model swap can be detected and re-indexed.
    private const string EmbeddingModelName = "bge-small-en-v1.5";

    // Bump when the embedding text-building contract (or model) changes so a re-index replaces vectors.
    private const int CurrentEmbeddingVersion = 1;

    public async Task ReindexAsync(CancellationToken ct = default)
    {
        if (!embeddingService.IsAvailable)
        {
            logger.LogInformation("Embedding service unavailable; skipping MCP embedding re-index.");
            return;
        }

        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableSemanticRetrieval)
        {
            logger.LogInformation("Semantic retrieval disabled; skipping MCP embedding re-index.");
            return;
        }

        List<int> dataSourceIds;
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            dataSourceIds = await context.DatabaseMetadata
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToListAsync(ct);
        }

        var failed = 0;
        foreach (var dataSourceId in dataSourceIds)
        {
            try
            {
                await IndexDataSourceAsync(dataSourceId, ct);
            }
            catch (OperationCanceledException)
            {
                // Job shutdown/cancellation must unwind — do not keep issuing work for later sources.
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Failed to index embeddings for data source {DataSourceId}", dataSourceId);
            }
        }

        // A per-source failure must not leave the vector store silently half-populated behind a green job.
        // If EVERY source failed (e.g. an unloadable model that slipped past IsAvailable), throw so Hangfire
        // marks the job Failed and it gets investigated (§6.4 — retries are off precisely for this reason).
        if (failed > 0 && failed == dataSourceIds.Count)
        {
            throw new InvalidOperationException(
                $"MCP embedding re-index failed for all {dataSourceIds.Count} data source(s) — vector store not populated.");
        }

        if (failed > 0)
        {
            logger.LogError("MCP embedding re-index completed with failures: {Failed} of {Total} data source(s) failed.", failed, dataSourceIds.Count);
        }
    }

    public async Task ReindexDataSourceAsync(int dataSourceId, CancellationToken ct = default)
    {
        if (!embeddingService.IsAvailable)
        {
            return;
        }

        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableSemanticRetrieval)
        {
            return;
        }

        await IndexDataSourceAsync(dataSourceId, ct);
    }

    private async Task IndexDataSourceAsync(int dataSourceId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var tables = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == dataSourceId)
            .Select(x =>
                new TableSource
                {
                    Id = x.Id,
                    SchemaName = x.SchemaName,
                    TableName = x.TableName,
                    TableDescription = x.TableDescription,
                    Columns = x.Columns
                        .Select(y =>
                            new ColumnSource
                            {
                                Id = y.Id,
                                ColumnName = y.ColumnName,
                                Description = y.Description
                            })
                        .ToList()
                })
            .ToListAsync(ct);

        // Retrieval-time selection now spans ALL lesson types (§ Architecture ⑧), so embed every valid
        // approved/auto-approved pattern as an Exemplar — not just CommonQuery. Patterns without an
        // ExampleQuestion (corrections, joins, doc-gaps) embed their PatternContent instead so they can be
        // ranked by masked-question similarity too. Superseded (stale) patterns are skipped: they are never
        // injected, so indexing their vectors would only waste space and risk a stale hit mapping back.
        var exemplars = await context.McpLearnedPatterns
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.SupersededAt == null)
            .Select(x =>
                new ExemplarSource
                {
                    Id = x.Id,
                    ExampleQuestion = x.ExampleQuestion,
                    PatternContent = x.PatternContent
                })
            .ToListAsync(ct);

        var inputs = BuildEmbeddingInputs(tables, exemplars);

        var existing = await context.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .ToListAsync(ct);

        // Prune stale vectors (§ Architecture ⑧): a row whose owner is no longer in the current valid set —
        // a superseded/rejected/reverted exemplar, OR a table/column that was dropped from metadata — is never
        // touched by the upsert path, so it lingers and occupies top-k slots in the NN query (which filters
        // only by data source + owner type), starving valid results. Delete every such row (exemplars AND
        // metadata) in this SAME unit of work (one SaveChanges, §5.7).
        var validExemplarIds = exemplars
            .Select(x => x.Id)
            .ToHashSet();
        var validTableIds = tables
            .Select(x => x.Id)
            .ToHashSet();
        var validColumnIds = tables
            .SelectMany(x => x.Columns)
            .Select(x => x.Id)
            .ToHashSet();
        var staleRows = existing
            .Where(x =>
                (x.OwnerType == McpEmbeddingOwnerType.Exemplar && !validExemplarIds.Contains(x.OwnerId))
                || (x.OwnerType == McpEmbeddingOwnerType.MetadataTable && !validTableIds.Contains(x.OwnerId))
                || (x.OwnerType == McpEmbeddingOwnerType.MetadataColumn && !validColumnIds.Contains(x.OwnerId)))
            .ToList();

        // Nothing to embed AND nothing to prune → no-op (guards the empty-set case without an idle SaveChanges).
        if (inputs.Count == 0 && staleRows.Count == 0)
        {
            return;
        }

        var existingByKey = existing.ToDictionary(x => (x.OwnerType, x.OwnerId));
        var dimensions = embeddingService.Dimensions;

        var toAdd = new List<McpEmbedding>();
        // (Row, vector) pairs to push into the DB-managed pgvector column after SaveChanges assigns ids.
        var vectorWrites = new List<(McpEmbedding Row, float[] Vector)>();
        if (inputs.Count > 0)
        {
            var texts = inputs
                .Select(x => x.Text)
                .ToList();
            var vectors = await embeddingService.EmbedBatchAsync(texts, ct);

            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var bytes = EmbeddingCodec.ToBytes(vectors[i]);

                if (existingByKey.TryGetValue((input.OwnerType, input.OwnerId), out var row))
                {
                    row.EmbeddingBytes = bytes;
                    row.Model = EmbeddingModelName;
                    row.Dimensions = dimensions;
                    row.EmbeddingVersion = CurrentEmbeddingVersion;
                    vectorWrites.Add((row, vectors[i]));
                }
                else
                {
                    var newRow = new McpEmbedding
                    {
                        DataSourceId = dataSourceId,
                        OwnerType = input.OwnerType,
                        OwnerId = input.OwnerId,
                        EmbeddingBytes = bytes,
                        Model = EmbeddingModelName,
                        Dimensions = dimensions,
                        EmbeddingVersion = CurrentEmbeddingVersion
                    };
                    toAdd.Add(newRow);
                    vectorWrites.Add((newRow, vectors[i]));
                }
            }
        }

        if (toAdd.Count > 0)
        {
            await context.McpEmbeddings.AddRangeAsync(toAdd, ct);
        }

        if (staleRows.Count > 0)
        {
            context.McpEmbeddings.RemoveRange(staleRows);
        }

        await context.SaveChangesAsync(ct);

        // Populate the DB-managed pgvector column now that new rows have DB-assigned ids (PostgreSQL only;
        // no-op on SQL Server / in-memory, which cosine over the byte column instead).
        await vectorWriter.WriteAsync(
            context,
            vectorWrites.Select(x => (x.Row.Id, x.Vector)).ToList(),
            ct);

        logger.LogInformation(
            "Indexed {Count} MCP embeddings for data source {DataSourceId} ({Added} new, {Pruned} stale vector(s) pruned)",
            inputs.Count, dataSourceId, toAdd.Count, staleRows.Count);
    }

    private static List<EmbeddingInput> BuildEmbeddingInputs(List<TableSource> tables, List<ExemplarSource> exemplars)
    {
        var inputs = new List<EmbeddingInput>();

        foreach (var table in tables)
        {
            inputs.Add(new EmbeddingInput(
                McpEmbeddingOwnerType.MetadataTable,
                table.Id,
                BuildTableText(table.SchemaName, table.TableName, table.TableDescription)));

            foreach (var column in table.Columns)
            {
                inputs.Add(new EmbeddingInput(
                    McpEmbeddingOwnerType.MetadataColumn,
                    column.Id,
                    BuildColumnText(table.TableName, column.ColumnName, column.Description)));
            }
        }

        foreach (var exemplar in exemplars)
        {
            // Prefer the natural-language ExampleQuestion; fall back to PatternContent for lessons that
            // carry no question (corrections/joins/doc-gaps) so they still get a similarity-rankable vector.
            inputs.Add(new EmbeddingInput(
                McpEmbeddingOwnerType.Exemplar,
                exemplar.Id,
                EmbeddingMaskingHelper.Mask(exemplar.ExampleQuestion ?? exemplar.PatternContent)));
        }

        return inputs;
    }

    /// <summary>Deterministic table embedding text: <c>"{schema}.{table}: {description}"</c> (description omitted when blank).</summary>
    internal static string BuildTableText(string schemaName, string tableName, string? description)
    {
        var qualified = $"{schemaName}.{tableName}";
        return string.IsNullOrWhiteSpace(description)
            ? qualified
            : $"{qualified}: {description}";
    }

    /// <summary>Deterministic column embedding text: <c>"{table}.{column}: {description}"</c> (description omitted when blank).</summary>
    internal static string BuildColumnText(string tableName, string columnName, string? description)
    {
        var qualified = $"{tableName}.{columnName}";
        return string.IsNullOrWhiteSpace(description)
            ? qualified
            : $"{qualified}: {description}";
    }

    private readonly record struct EmbeddingInput(McpEmbeddingOwnerType OwnerType, int OwnerId, string Text);

    private sealed class TableSource
    {
        public int Id { get; set; }
        public string SchemaName { get; set; } = null!;
        public string TableName { get; set; } = null!;
        public string? TableDescription { get; set; }
        public List<ColumnSource> Columns { get; set; } = [];
    }

    private sealed class ColumnSource
    {
        public int Id { get; set; }
        public string ColumnName { get; set; } = null!;
        public string? Description { get; set; }
    }

    private sealed class ExemplarSource
    {
        public int Id { get; set; }
        public string? ExampleQuestion { get; set; }
        public string PatternContent { get; set; } = null!;
    }
}
