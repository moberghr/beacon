using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Provider-neutral vector store for MCP self-learning. The embedding is persisted as
/// <see cref="EmbeddingBytes"/> (bytea on PostgreSQL, varbinary(max) on SQL Server) so the
/// shared <c>BeaconContext</c> model stays provider-neutral. On PostgreSQL a DB-managed
/// <c>vector(384)</c> column + HNSW index live alongside this row (added in raw SQL by the
/// migration, invisible to the EF model); SQL Server does in-memory cosine over the bytes.
/// </summary>
public class McpEmbedding : BaseEntity
{
    public int DataSourceId { get; set; }
    public McpEmbeddingOwnerType OwnerType { get; set; }
    public int OwnerId { get; set; }

    public required byte[] EmbeddingBytes { get; set; }
    public required string Model { get; set; }
    public int Dimensions { get; set; }
    public int EmbeddingVersion { get; set; }
}
