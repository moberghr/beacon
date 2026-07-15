using Beacon.AI.Services.Embeddings;
using Beacon.Core.Data;

namespace Beacon.Tests.Common;

/// <summary>
/// Test double for <see cref="IEmbeddingVectorColumnWriter"/>. Captures the (id, vector) pairs the indexing
/// services hand off for the DB-managed pgvector column so tests can assert the write set without opening a
/// database (the real writer issues raw pgvector SQL). No-op otherwise.
/// </summary>
internal sealed class FakeVectorColumnWriter : IEmbeddingVectorColumnWriter
{
    public List<(int Id, float[] Vector)> Writes { get; } = [];

    public Task WriteAsync(BeaconContext context, IReadOnlyList<(int Id, float[] Vector)> writes, CancellationToken ct)
    {
        Writes.AddRange(writes);
        return Task.CompletedTask;
    }
}
