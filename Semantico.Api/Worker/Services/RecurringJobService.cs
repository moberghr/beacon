using Hangfire;

namespace Semantico.Api.Worker.Services;

public class RecurringJobService : IRecurringJobService
{
    private readonly IRecurringJobManager _recurringJobManager;

    public RecurringJobService(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate<T>(int queryId, string name, string cron) where T : IJobService
    {
        _recurringJobManager.AddOrUpdate<T>(name, x => x.ExecuteQuery(queryId), cron);
    }
}

public interface IRecurringJobService
{
    public void AddOrUpdate<T>(int queryId, string name, string cron)
        where T : IJobService;
}

