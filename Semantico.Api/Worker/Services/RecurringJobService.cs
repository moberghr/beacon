using Hangfire;

namespace Semantico.Api.Worker.Services;

public class RecurringJobService : IRecurringJobService
{
    private readonly IRecurringJobManager _recurringJobManager;

    public RecurringJobService(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int queryId, string name, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(name, x => x.ExecuteQuery(queryId), cron);
    }
}

public interface IRecurringJobService
{
    public void AddOrUpdate(int queryId, string name, string cron);
}