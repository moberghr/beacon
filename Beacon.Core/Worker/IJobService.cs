using Hangfire;

namespace Beacon.Core.Worker;

public interface IJobService
{
    // IJobCancellationToken lets Hangfire inject a shutdown-aware token so
    // long-running subscription queries can be cancelled cleanly.
    Task ExecuteQuery(int subscriptionId, IJobCancellationToken cancellationToken);

    Task EvaluateDataContract(int contractId);

    Task AggregateLearnedPatterns();

    Task GenerateDocumentationPatches();

    Task CleanupOldSignals();
}