using Hangfire;
using Semantico.Core.Worker;

namespace Semantico.SampleProject.Services;

/// <summary>
/// One possible implementation of the semantico schedule execute query
/// </summary>
public class SemanticoScheduler : ISemanticoScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public SemanticoScheduler(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(CompileSubscriptionJobKey(subscriptionId, subscriptionName), x => x.ExecuteQuery(subscriptionId), cron);
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        _recurringJobManager.RemoveIfExists(CompileSubscriptionJobKey(subscriptionId, subscriptionName));
    }

    public void AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(
            CompileDataQualityJobKey(contractId, contractName),
            x => x.EvaluateDataContract(contractId),
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