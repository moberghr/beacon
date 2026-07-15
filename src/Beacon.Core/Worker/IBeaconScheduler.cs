namespace Beacon.Core.Worker;

public interface IBeaconScheduler
{
    Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron);

    Task Remove(int subscriptionId, string subscriptionName);

    Task AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
        => throw new NotImplementedException("Data quality scheduling not implemented in this host");

    Task RemoveDataQualityJob(int contractId, string contractName)
        => throw new NotImplementedException("Data quality scheduling not implemented in this host");

    // Fire-and-forget background work used by the AI features. Implementations enqueue the
    // job on the host's job runner and return its job id (the UI correlates JobStatusChanged
    // push events by this id). notifyUserId, when set, identifies the user who should receive
    // those push events.
    Task<string> EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId)
        => throw new NotImplementedException("Background enqueue not implemented in this host");

    Task<string> EnqueueAiActorThinkCycle(int actorId, int subscriptionId)
        => throw new NotImplementedException("Background enqueue not implemented in this host");

    // Fire-and-forget execution of an already-created eval run (see IMcpEvalService.StartRunAsync).
    // Implementations enqueue IJobService.RunMcpEval(runId) on the host's job runner.
    Task<string> EnqueueMcpEval(int runId)
        => throw new NotImplementedException("Background enqueue not implemented in this host");
}
