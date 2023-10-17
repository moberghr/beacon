using Hangfire;

namespace Semantico.Api.Worker.Services;

public class RecurringJobService : IRecurringJobService
{
    private readonly IRecurringJobManager _recurringJobManager;

    public RecurringJobService(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, int queryId, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(subscriptionId.ToString(), x => x.ExecuteQuery(queryId), cron);
    }

    public void Remove(int subscriptionId)
    {
        _recurringJobManager.RemoveIfExists(subscriptionId.ToString());
    }
}

public interface IRecurringJobService
{
    public void AddOrUpdate(int subscriptionId, int queryId, string cron);

    public void Remove(int subscriptionId);
}