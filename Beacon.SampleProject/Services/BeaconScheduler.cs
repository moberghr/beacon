using Hangfire;
using Beacon.Core.Worker;

namespace Beacon.SampleProject.Services;

/// <summary>
/// One possible implementation of the beacon schedule execute query
/// </summary>
public class BeaconScheduler : IBeaconScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public BeaconScheduler(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
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

    private static string CompileSubscriptionJobKey(int subscriptionId, string subscriptionName)
    {
        return $"{subscriptionId} - {subscriptionName}";
    }

    private static string CompileDataQualityJobKey(int contractId, string contractName)
    {
        return $"dq-{contractId} - {contractName}";
    }
}