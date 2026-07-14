using Microsoft.EntityFrameworkCore;
using Beacon.Core.Helpers;

namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// Populates the DB-managed pgvector <c>embedding</c> column on <c>mcp_embeddings</c> for rows already
/// persisted with their <c>EmbeddingBytes</c>. The vector column is deliberately invisible to the EF model
/// (Beacon.Core stays provider-neutral and no Pgvector handler is registered), so it is written separately.
/// Injected (rather than a static call) so the indexing unit tests can substitute a no-op and stay off any
/// real database.
/// </summary>
internal interface IEmbeddingVectorColumnWriter
{
    Task WriteAsync(BeaconContext context, IReadOnlyList<(int Id, float[] Vector)> writes, CancellationToken ct);
}

/// <summary>
/// PostgreSQL implementation: one parameterized set-based UPDATE via <c>unnest</c>. On any non-Npgsql
/// provider (SQL Server / in-memory retrieval, which cosines over the byte column) it is a no-op, since
/// those providers have no vector column to write.
/// </summary>
internal sealed class PgVectorColumnWriter : IEmbeddingVectorColumnWriter
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Must equal the vector(N) modifier on mcp_embeddings.embedding (migration 20260709120200). pgvector
    // rejects a literal of a different length, so validate up-front for an actionable error instead of an
    // opaque cast failure deep inside the batch UPDATE.
    private const int VectorDimensions = 384;

    public async Task WriteAsync(BeaconContext context, IReadOnlyList<(int Id, float[] Vector)> writes, CancellationToken ct)
    {
        if (writes.Count == 0 || context.Database.ProviderName != NpgsqlProviderName)
        {
            return;
        }

        foreach (var write in writes)
        {
            if (write.Vector.Length != VectorDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding for mcp_embeddings id {write.Id} has {write.Vector.Length} dimension(s); expected {VectorDimensions} (column is vector({VectorDimensions})).");
            }
        }

        var ids = writes
            .Select(x => x.Id)
            .ToArray();
        var literals = writes
            .Select(x => EmbeddingCodec.ToVectorLiteral(x.Vector))
            .ToArray();

        // One round-trip for the whole batch via unnest. ids/literals are our own values passed as array
        // parameters (never string-interpolated) and each literal is cast to vector(384) (§1.10). The table
        // is unqualified and resolves via the connection search_path, matching the retrieval-side query.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE mcp_embeddings AS m
               SET embedding = data.emb::vector(384)
               FROM unnest({ids}, {literals}) AS data(id, emb)
               WHERE m.id = data.id",
            ct);
    }
}
