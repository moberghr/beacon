namespace Beacon.Core.Services;

public interface IMcpLearningAggregationService
{
    Task AggregateLearnedPatternsAsync(CancellationToken ct = default);
    Task GenerateDocumentationPatchesAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes learning signals older than their retention window, whether learning is on or off. With
    /// <paramref name="retentionDays"/> <c>0</c> (the default) each project's signals use that project's effective
    /// window and signals without a project use the global window; a positive value overrides every window. A stored
    /// window below one day falls back to the global window (a project's) or the default (the global one). A project whose
    /// signals could not be cleaned up does not stop the others; the call then throws once every project was tried.
    /// </summary>
    Task CleanupOldSignalsAsync(int retentionDays = 0, CancellationToken ct = default);
}
