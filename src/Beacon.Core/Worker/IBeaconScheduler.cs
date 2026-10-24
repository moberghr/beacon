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
    // those push events: the hub user id, the enqueuing actor's UserId (Users.ExternalId), which a
    // host publishes to with Clients.User(notifyUserId).
    Task<string> EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId)
        => throw new NotImplementedException("Background enqueue not implemented in this host");

    Task<string> EnqueueAiActorThinkCycle(int actorId, int subscriptionId)
        => throw new NotImplementedException("Background enqueue not implemented in this host");

    // Fire-and-forget execution of an already-created eval run (see IMcpEvalService.StartRunAsync).
    // Implementations enqueue IJobService.RunMcpEval(runId) on the host's job runner.
    Task<string> EnqueueMcpEval(int runId)
        => throw new NotImplementedException("Background enqueue not implemented in this host");

    // Fire-and-forget re-scan of a data source's metadata (IDatabaseMetadataService.RefreshMetadataAsync, forced), enqueued
    // after its connection was pointed at another target. Returns the job id, or null when this host runs no such job (the
    // caller logs that the old target's schema stays until a refresh).
    Task<string?> EnqueueMetadataRefresh(int dataSourceId)
        => Task.FromResult<string?>(null);

    // Fire-and-forget run of a recorded background task (Beacon.Core.BackgroundTasks): the host's job calls
    // IBackgroundTaskService.ExecuteAsync(taskId). notifyUserId, when set, is the user who receives the job's JobStatusChanged
    // push events. Returns the job id, or null when this host runs no such job: the task then runs in the request that
    // started it.
    Task<string?> EnqueueBackgroundTask(int taskId, string? notifyUserId)
        => Task.FromResult<string?>(null);
}
