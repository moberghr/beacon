using Microsoft.EntityFrameworkCore;
using Beacon.Core.Worker;
using Beacon.SampleProject.Warp;
using Beacon.SampleProject.Warp.Jobs;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Helper;

namespace Beacon.SampleProject.Services;

/// <summary>
/// Warp-backed implementation of <see cref="IBeaconScheduler"/>. Recurring subscription and
/// data-quality work becomes Warp recurring-job definitions; fire-and-forget AI work becomes
/// enqueued Warp jobs. Returns the Warp job id (a GUID) as the string the UI correlates
/// JobStatusChanged push events by. Recurring jobs are keyed by name — removal looks the
/// definition up by name via the Warp context (Warp's DeleteRecurringJob is id-based).
/// </summary>
public sealed class BeaconScheduler(
    IPublisher publisher,
    IRecurringJobPublisher recurringJobPublisher,
    WarpDbContext warpDbContext) : IBeaconScheduler
{
    public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
        => recurringJobPublisher.AddOrUpdateRecurringJob(
            new ExecuteSubscriptionQueryJob { SubscriptionId = subscriptionId },
            CompileSubscriptionJobKey(subscriptionId, subscriptionName),
            cron);

    public Task Remove(int subscriptionId, string subscriptionName)
        => RemoveRecurringJobByName(CompileSubscriptionJobKey(subscriptionId, subscriptionName));

    public Task AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
        => recurringJobPublisher.AddOrUpdateRecurringJob(
            new EvaluateDataContractJob { ContractId = contractId },
            CompileDataQualityJobKey(contractId, contractName),
            cron);

    public Task RemoveDataQualityJob(int contractId, string contractName)
        => RemoveRecurringJobByName(CompileDataQualityJobKey(contractId, contractName));

    public async Task<string> EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId)
    {
        var jobParameters = new JobParameters();

        // Tag the job with the enqueueing user so JobStatusChangedBehavior publishes
        // JobStatusChanged events to /beacon/api/hub for that user only (PII guard, §1.11).
        if (!string.IsNullOrWhiteSpace(notifyUserId))
        {
            jobParameters.Metadata = new Dictionary<string, object> { ["BeaconUserId"] = notifyUserId };
        }

        var jobId = await publisher.Enqueue(new GenerateProjectDocumentationJob { ProjectId = projectId, UserId = userId }, jobParameters);
        await publisher.SaveChangesAsync();

        return jobId.ToString();
    }

    public async Task<string> EnqueueAiActorThinkCycle(int actorId, int subscriptionId)
    {
        var jobId = await publisher.Enqueue(new AiActorThinkCycleJob { ActorId = actorId, SubscriptionId = subscriptionId });
        await publisher.SaveChangesAsync();

        return jobId.ToString();
    }

    public async Task<string> EnqueueMcpEval(int runId)
    {
        var jobId = await publisher.Enqueue(new RunMcpEvalJob { RunId = runId });
        await publisher.SaveChangesAsync();

        return jobId.ToString();
    }

    private async Task RemoveRecurringJobByName(string name)
    {
        var recurringJob = await warpDbContext.Set<RecurringJob>()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync();
        if (recurringJob == null)
        {
            return;
        }

        warpDbContext.Set<RecurringJob>().Remove(recurringJob);
        await warpDbContext.SaveChangesAsync();
    }

    private static string CompileSubscriptionJobKey(int subscriptionId, string subscriptionName)
        => $"{subscriptionId} - {subscriptionName}";

    private static string CompileDataQualityJobKey(int contractId, string contractName)
        => $"dq-{contractId} - {contractName}";
}
