using Hangfire;

namespace Beacon.Core.Worker;

public interface IJobService
{
    // IJobCancellationToken lets Hangfire inject a shutdown-aware token so
    // long-running background work can be cancelled cleanly on graceful shutdown.
    Task ExecuteQuery(int subscriptionId, IJobCancellationToken cancellationToken);

    Task EvaluateDataContract(int contractId, IJobCancellationToken cancellationToken);

    Task AggregateLearnedPatterns(IJobCancellationToken cancellationToken);

    Task GenerateDocumentationPatches(IJobCancellationToken cancellationToken);

    Task CleanupOldSignals(IJobCancellationToken cancellationToken);
}