using Hangfire;

namespace Semantico.Api.Worker.Services;

public class RecurringJobService : IRecurringJobService
{
    private readonly IRecurringJobManager _recurringJobManager;

    public RecurringJobService(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(subscriptionId.ToString(), x => x.ExecuteQuery(subscriptionId), cron);
    }

    public void Remove(int subscriptionId)
    {
        _recurringJobManager.RemoveIfExists(subscriptionId.ToString());
    }
}

public interface IRecurringJobService
{
    public void AddOrUpdate(int subscriptionId, string cron);

    public void Remove(int subscriptionId);
}