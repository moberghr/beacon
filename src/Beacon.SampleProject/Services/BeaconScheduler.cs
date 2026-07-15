using Hangfire;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Documentation;
using Beacon.Core.Worker;

namespace Beacon.SampleProject.Services;

/// <summary>
/// One possible implementation of the beacon schedule execute query
/// </summary>
public class BeaconScheduler : IBeaconScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public BeaconScheduler(IRecurringJobManager recurringJobManager, IBackgroundJobClient backgroundJobClient)
    {
        _recurringJobManager = recurringJobManager;
        _backgroundJobClient = backgroundJobClient;
    }

    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(
            CompileSubscriptionJobKey(subscriptionId, subscriptionName),
            // CancellationToken.None is a placeholder — Hangfire substitutes its
            // shutdown-aware token for CancellationToken parameters at execution time.
            x => x.ExecuteQuery(subscriptionId, CancellationToken.None),
            cron);
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        _recurringJobManager.RemoveIfExists(CompileSubscriptionJobKey(subscriptionId, subscriptionName));
    }

    public void AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(
            CompileDataQualityJobKey(contractId, contractName),
            x => x.EvaluateDataContract(contractId, CancellationToken.None),
            cron);
    }

    public void RemoveDataQualityJob(int contractId, string contractName)
    {
        _recurringJobManager.RemoveIfExists(CompileDataQualityJobKey(contractId, contractName));
    }

    public string EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId)
    {
        var jobId = _backgroundJobClient.Enqueue<IProjectDocumentationService>(
            x => x.GenerateDocumentationAsync(projectId, userId, CancellationToken.None));

        // Tag the job with the enqueueing user so HangfireSignalRJobFilter publishes
        // JobStatusChanged events to /beacon/api/hub for that user only.
        if (!string.IsNullOrWhiteSpace(notifyUserId))
        {
            JobStorage.Current.GetConnection().SetJobParameter(jobId, "BeaconUserId", notifyUserId);
        }

        return jobId;
    }

    public string EnqueueAiActorThinkCycle(int actorId, int subscriptionId)
    {
        return _backgroundJobClient.Enqueue<IAiActorServiceExtended>(
            x => x.ExecuteThinkCycleBackgroundAsync(actorId, subscriptionId));
    }

    public string EnqueueMcpEval(int runId)
    {
        // CancellationToken.None is a placeholder — Hangfire substitutes its shutdown-aware
        // token for CancellationToken parameters at execution time.
        return _backgroundJobClient.Enqueue<IJobService>(
            x => x.RunMcpEval(runId, CancellationToken.None));
    }

    private static string CompileSubscriptionJobKey(int subscriptionId, string subscriptionName)
    {
        return $"{subscriptionId} - {subscriptionName}";
    }

    private static string CompileDataQualityJobKey(int contractId, string contractName)
    {
        return $"dq-{contractId} - {contractName}";
    }
}