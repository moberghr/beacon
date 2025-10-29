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

    private static string CompileSubscriptionJobKey(int subscriptionId, string subscriptionName)
    {
        return $"{subscriptionId} - {subscriptionName}";
    }
}