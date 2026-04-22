namespace Beacon.Core.Worker;

public interface IJobService
{
    Task ExecuteQuery(int subscriptionId);

    Task EvaluateDataContract(int contractId);

    Task AggregateLearnedPatterns();

    Task GenerateDocumentationPatches();

    Task CleanupOldSignals();
}