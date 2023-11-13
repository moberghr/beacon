using Hangfire;

namespace Semantico.Api.Worker.Services;

public class RecurringJobService : IRecurringJobService
{
    private readonly IRecurringJobManager _recurringJobManager;

    public RecurringJobService(IRecurringJobManager recurringJobManager)
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

public interface IRecurringJobService
{
    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron);

    public void Remove(int subscriptionId, string subscriptionName);
}