using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// Builds and caches the per-data-source <see cref="SchemaGraph"/>. Consumed by the schema-health
/// handlers in <c>Beacon.Core</c> and by the prompt builder in <c>Beacon.AI</c> — which is why the graph
/// lives in Core: a graph in <c>Beacon.AI</c> would force an illegal Core → AI reference (§2.4).
/// </summary>
public interface ISchemaGraphService
{
    Task<SchemaGraph> GetGraphAsync(int dataSourceId, CancellationToken cancellationToken = default);

    Task<SchemaHealthReport> GetHealthAsync(int dataSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the cached graph for one data source. Every relationship write must call this, or the graph
    /// keeps serving pre-edit joins (§6.2 pairs a cache with its invalidation).
    /// </summary>
    void Invalidate(int dataSourceId);
}
