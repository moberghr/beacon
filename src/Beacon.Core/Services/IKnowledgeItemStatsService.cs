namespace Beacon.Core.Services;

/// <summary>
/// Keeps a project's knowledge item counters (<c>McpKnowledgeItemStats</c>) in step with its <c>ask</c> signals. The
/// learning job calls it once per project after the pattern aggregation. Only the project's own signals count (§1.12),
/// and only ids, enums, flags and the caller key are read (§1.11). The implementation lives in Beacon.AI.
/// </summary>
public interface IKnowledgeItemStatsService
{
    /// <summary>
    /// Recomputes the counters of <paramref name="projectId"/> from its <c>ask</c> signals inside the learning-signal
    /// retention window, the window the learning job mines in: a row per knowledge item those signals carried is written or
    /// updated, and the rows of items none of them carried are deleted, in one save.
    /// </summary>
    Task RecomputeAsync(int projectId, CancellationToken ct);
}
