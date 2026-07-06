namespace Beacon.Core.Worker;

public interface IJobService
{
    // The host's scheduler passes a shutdown-aware token so long-running
    // background work can be cancelled cleanly on graceful shutdown.
    Task ExecuteQuery(int subscriptionId, CancellationToken cancellationToken);

    Task EvaluateDataContract(int contractId, CancellationToken cancellationToken);

    Task AggregateLearnedPatterns(CancellationToken cancellationToken);

    Task GenerateDocumentationPatches(CancellationToken cancellationToken);

    Task CleanupOldSignals(CancellationToken cancellationToken);
}
